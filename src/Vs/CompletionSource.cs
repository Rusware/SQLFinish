using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using SQLFinish.Engine;

namespace SQLFinish.Vs
{
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("SQLFinish")]
    [ContentType("SQL")] // the content type of SSMS's query editor
    internal sealed class CompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView)
        {
            Ssms.Trace("source requested for a " + textView.TextBuffer.ContentType.TypeName + " view");
            return textView.Properties.GetOrCreateSingletonProperty(() => new CompletionSource());
        }
    }

    /// <summary>
    /// Tab straight after a select-list "*" (or "alias.*") replaces it with the column list and lays that
    /// statement out again, one column per line.
    /// </summary>
    [Export(typeof(ICommandHandler))]
    [Name("SQLFinish expand star")]
    [ContentType("SQL")]
    [Order(Before = PredefinedCompletionNames.CompletionCommandHandler)]
    internal sealed class ExpandStarHandler : ICommandHandler<TabKeyCommandArgs>
    {
        public string DisplayName => "SQLFinish: expand * into columns";

        public CommandState GetCommandState(TabKeyCommandArgs args) => CommandState.Unspecified;

        public bool ExecuteCommand(TabKeyCommandArgs args, CommandExecutionContext executionContext)
        {
            var view = args.TextView;
            var caret = view.Caret.Position.BufferPosition;
            if (!view.Selection.IsEmpty || caret.Position == 0 || caret.Snapshot[caret.Position - 1] != '*') return false;

            string text = caret.Snapshot.GetText();
            var connection = Ssms.ActiveConnection();
            // Waits (briefly, see SchemaCache) if the catalog is not in memory yet.
            var schema = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(
                () => SchemaCache.GetAsync(connection, Completion.DatabaseAt(text, caret.Position), CancellationToken.None));
            var edit = Completion.ExpandStar(text, caret.Position, schema);
            Ssms.Trace("tab after * -> " + (edit == null ? "nothing to expand" : edit.Text));
            if (edit == null) return false; // an ordinary Tab
            Ssms.InsertColumns(view, text, edit.Start, edit.Length, edit.Text);
            return true;
        }
    }

    /// <summary>Lets the "Pick columns..." suggestion open the picker instead of inserting text.</summary>
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [Name("SQLFinish commit")]
    [ContentType("SQL")]
    internal sealed class CommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new CommitManager());
    }

    internal sealed class CommitManager : IAsyncCompletionCommitManager
    {
        // A list belongs to the word being typed. Left alone, the editor keeps it open across whatever is typed
        // next ("BY " still matches BY), so the list that should follow never opens: no columns after
        // "ORDER BY ", none after "o.". So a character that ends the word closes the list, inserting nothing,
        // and is then typed as usual, free to open the next list. Only Tab and Enter insert a suggestion.
        const string WordEnders = " .,()=<>!+-*/%;'\"]";

        public System.Collections.Generic.IEnumerable<char> PotentialCommitCharacters => WordEnders;

        public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => true;

        public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token)
        {
            if (WordEnders.IndexOf(typedChar) >= 0) return CommitResult.Handled; // close the list; the character is typed next
            if (item.DisplayText != Completion.PickColumnsText || !(session.TextView is IWpfTextView view)) return CommitResult.Unhandled;
            var typed = session.ApplicableToSpan.GetSpan(buffer.CurrentSnapshot);
            if (!typed.IsEmpty) buffer.Delete(typed.Span);
            // Open the dialog only after this commit has returned and the completion session has closed.
#pragma warning disable VSSDK007 // fire and forget: the dialog is the whole point, nothing waits on it
            Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Yield();
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ColumnPicker.Show(view);
            }).FileAndForget("SQLFinish/PickColumns");
#pragma warning restore VSSDK007
            return CommitResult.Handled;
        }
    }

    internal sealed class CompletionSource : IAsyncCompletionSource
    {
        const string DetailKey = "SQLFinish.Detail";
        Connection connection; // read on the UI thread when a session starts, used on the background thread

        static ImageElement Icon(int id) => new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, id));

        static readonly ImageElement[] Icons =
        {
            Icon(KnownImageIds.Link),                 // Join
            Icon(KnownImageIds.CheckBoxList),         // Action: the column picker
            Icon(KnownImageIds.Column),
            Icon(KnownImageIds.Table),                // Alias
            Icon(KnownImageIds.Table),
            Icon(KnownImageIds.View),
            Icon(KnownImageIds.StoredProcedure),
            Icon(KnownImageIds.Method),               // Function
            Icon(KnownImageIds.Schema),
            Icon(KnownImageIds.Database),
            Icon(KnownImageIds.Snippet),
            Icon(KnownImageIds.IntellisenseKeyword),
        };

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            if (trigger.Reason == CompletionTriggerReason.Backspace || trigger.Reason == CompletionTriggerReason.Deletion)
                return CompletionStartData.DoesNotParticipateInCompletion;

            int caret = triggerLocation.Position;
            // ponytail: re-reads the whole script on each trigger; analyse a window around the caret if huge scripts ever lag.
            var ctx = Completion.Analyze(triggerLocation.Snapshot.GetText(), caret);
            Ssms.Trace("trigger " + trigger.Reason + " '" + trigger.Character + "' -> " + ctx.Mode);
            if (ctx.Mode == Mode.None) return CompletionStartData.DoesNotParticipateInCompletion;

            if (trigger.Reason == CompletionTriggerReason.Insertion)
            {
                char c = trigger.Character;
                bool wanted = c == ' ' ? ctx.OnSpace
                    : c == '.' ? ctx.Mode == Mode.Qualified
                    : (char.IsLetter(c) || c == '_' || c == '[') && ctx.Auto;
                if (!wanted) return CompletionStartData.DoesNotParticipateInCompletion;
            }

            connection = Ssms.ActiveConnection();
            return new CompletionStartData(CompletionParticipation.ProvidesItems,
                new SnapshotSpan(triggerLocation.Snapshot, Span.FromBounds(ctx.PrefixStart, caret)));
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger,
            SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
        {
            string text = triggerLocation.Snapshot.GetText();
            int caret = triggerLocation.Position;
            var ctx = Completion.Analyze(text, caret);
            var schema = await SchemaCache.GetAsync(connection, Completion.DatabaseAt(text, caret), token).ConfigureAwait(false);

            Ssms.Trace("listing " + ctx.Mode + " with " + schema.Objects.Count + " catalog objects");
            int order = 0;
            var items = Completion.Items(ctx, schema).Select(i =>
            {
                // sortText keeps the engine's order: joins, columns, tables ... keywords
                var item = new CompletionItem(i.Text, this, Icons[(int)i.Kind], ImmutableArray<CompletionFilter>.Empty, suffix: "",
                    insertText: i.Insert, sortText: (order++).ToString("D6"), filterText: i.Filter ?? i.Text, attributeIcons: ImmutableArray<ImageElement>.Empty);
                item.Properties.AddProperty(DetailKey, i.Detail ?? "");
                return item;
            });
            return new CompletionContext(items.ToImmutableArray());
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token) =>
            Task.FromResult<object>(item.Properties.TryGetProperty(DetailKey, out string detail) ? detail : "");
    }
}
