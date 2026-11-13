using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SQLFinish.Engine;
using Task = System.Threading.Tasks.Task;

namespace SQLFinish.Vs
{
    /// <summary>Hosts the SQLFinish menu. Completion needs no package: it is a MEF part (CompletionSource.cs).</summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("SQLFinish", "SQL completion, formatting and snippets for SQL Server Management Studio.", "1.5.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)] // the Execute guard must be in place before the first query runs
    [Guid(PackageGuid)]
    public sealed class SqlFinishPackage : AsyncPackage
    {
        // Keep in step with SQLFinish.vsct.
        public const string PackageGuid = "7047c1ca-53e4-4ce3-9d2e-b18c1d8ae74d";
        static readonly Guid CommandSet = new Guid("8cbfe379-0252-43d9-9606-626c4a633c94");
        const int FormatCommand = 0x0100, RefreshCommand = 0x0101, PickColumnsCommand = 0x0102, HistoryCommand = 0x0103, DefinitionCommand = 0x0104;
        EnvDTE.CommandEvents executeEvents; // held so the event subscription stays alive
        string lastRemembered;              // so pressing F5 again on the same script adds nothing to the history

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var commands = (IMenuCommandService)await GetServiceAsync(typeof(IMenuCommandService));
            if (commands == null) return;
            commands.AddCommand(new MenuCommand((s, e) => Format(), new CommandID(CommandSet, FormatCommand)));
            commands.AddCommand(new MenuCommand((s, e) =>
            {
                var view = ActiveView();
                if (view != null && view.TextBuffer.ContentType.IsOfType("SQL")) ColumnPicker.Show(view);
                else Status("SQLFinish: click in a query window first.");
            }, new CommandID(CommandSet, PickColumnsCommand)));
            commands.AddCommand(new MenuCommand((s, e) =>
            {
                var view = ActiveView();
                HistoryWindow.Show(view != null && view.TextBuffer.ContentType.IsOfType("SQL") ? view : null);
            }, new CommandID(CommandSet, HistoryCommand)));
            commands.AddCommand(new MenuCommand((s, e) => OpenDefinition(), new CommandID(CommandSet, DefinitionCommand)));
            commands.AddCommand(new MenuCommand((s, e) =>
            {
                SchemaCache.Clear();
                Status("SQLFinish: suggestions will be reloaded from the database.");
            }, new CommandID(CommandSet, RefreshCommand)));

            try
            {
                var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE)) ?? throw new InvalidOperationException("no DTE service");
                var execute = dte.Commands.Item("Query.Execute");
                executeEvents = dte.Events.CommandEvents[execute.Guid, execute.ID];
                executeEvents.BeforeExecute += BeforeExecute;
            }
            catch (Exception e)
            {
                Ssms.Log("cannot watch Query.Execute, so there will be no warning for UPDATE / DELETE without WHERE", e);
            }
        }

        /// <summary>Runs just before SSMS executes the active query window: the last chance to stop it.</summary>
        void BeforeExecute(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var view = ActiveView();
                if (view == null || !view.TextBuffer.ContentType.IsOfType("SQL")) return;
                string sql = view.TextBuffer.CurrentSnapshot.GetText(ScriptSpan(view)); // what SSMS will run: the selection, else everything

                var unfiltered = Guard.Unfiltered(sql);
                if (unfiltered.Count > 0)
                {
                    int answer = VsShellUtilities.ShowMessageBox(this,
                        "No WHERE clause, so every row is affected:\n\n    " + string.Join("\n    ", unfiltered) + "\n\nExecute anyway?",
                        "SQLFinish", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
                    if (answer != 6 /* IDYES */)
                    {
                        cancelDefault = true;
                        Status("SQLFinish: execution cancelled.");
                        return;
                    }
                }
                if (Guard.ChangesSchema(sql)) SchemaCache.ClearForSchemaChange();
                Remember(sql);
            }
            catch (Exception e)
            {
                Ssms.Log("could not check the script before execution", e); // never block a query because the check itself failed
            }
        }

        /// <summary>
        /// Opens the procedure, view, function or trigger named at the caret in a new query window, as a
        /// script that modifies it.
        /// </summary>
        void OpenDefinition()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = ActiveView();
            if (view == null || !view.TextBuffer.ContentType.IsOfType("SQL"))
            {
                Status("SQLFinish: click in a query window first.");
                return;
            }
            var caret = view.Caret.Position.BufferPosition;
            string text = caret.Snapshot.GetText();
            var parts = Definition.NameAt(text, caret.Position);
            var connection = Ssms.ActiveConnection();
            if (parts == null || connection == null)
            {
                Status(parts == null ? "SQLFinish: put the caret in the name of a procedure, view or function first." : "SQLFinish: this window is not connected.");
                return;
            }
            string shown = string.Join(".", parts);
            string database = parts.Count == 3 ? parts[0] : Completion.DatabaseAt(text, caret.Position) ?? connection.Database ?? "";
            Status("SQLFinish: looking up " + shown + "...");
#pragma warning disable VSSDK007 // fire and forget: the query runs off the UI thread so a slow server cannot freeze SSMS
            JoinableTaskFactory.RunAsync(async () =>
            {
                Module module;
                try
                {
                    module = await System.Threading.Tasks.Task.Run(() =>
                    {
                        using (var cn = connection.Open(database)) return Definition.Load(cn, parts);
                    });
                }
                catch (Exception e)
                {
                    Ssms.Log("could not read the definition of " + shown, e);
                    return;
                }
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (module == null) Status("SQLFinish: there is no object named " + shown + " in " + database + ".");
                else if (module.Text == null) Status("SQLFinish: " + shown + " has no source to open (only procedures, views, functions and triggers do, unless encrypted).");
                else NewQuery(Definition.ModifyScript(module), module.Schema + "." + module.Name);
            }).FileAndForget("SQLFinish/OpenDefinition");
#pragma warning restore VSSDK007
        }

        /// <summary>Opens a new query window on the active window's connection, holding <paramref name="script"/>.</summary>
        void NewQuery(string script, string what)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (EnvDTE.DTE)GetService(typeof(EnvDTE.DTE));
            if (dte == null) return;
            var before = dte.ActiveDocument;
            dte.ExecuteCommand("File.NewQuery"); // SSMS's own Ctrl+N: same server and login as the window we came from
            var opened = dte.ActiveDocument;
            // Never write into the window we came from: if no new one appeared, give up.
            if (opened == null || (before != null && opened.FullName == before.FullName) || !(opened.Object("TextDocument") is EnvDTE.TextDocument document))
            {
                Status("SQLFinish: could not open a new query window for " + what + ".");
                return;
            }
            document.StartPoint.CreateEditPoint().Insert(script);
            document.Selection.StartOfDocument();
            Status("SQLFinish: opened " + what + ".");
        }

        /// <summary>Adds a script that is about to run to the history.</summary>
        void Remember(string sql)
        {
            // ponytail: scripts over a million characters (bulk data loads) are not kept; writing them would stall F5.
            if (sql.Trim().Length == 0 || sql.Length > 1000000 || sql == lastRemembered) return;
            try
            {
                var connection = Ssms.ActiveConnection();
                History.Save(History.Folder, new HistoryEntry { When = DateTime.Now, Server = connection?.Server, Database = connection?.Database, Sql = sql });
                lastRemembered = sql;
            }
            catch (Exception e)
            {
                Ssms.Log("could not add the script to the history", e);
            }
        }

        /// <summary>The selection, or the whole script when nothing is selected.</summary>
        static Span ScriptSpan(IWpfTextView view) =>
            view.Selection.IsEmpty ? new Span(0, view.TextBuffer.CurrentSnapshot.Length) : view.Selection.StreamSelectionSpan.SnapshotSpan.Span;

        /// <summary>Formats the selection, or the whole script when nothing is selected.</summary>
        void Format()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = ActiveView();
            if (view == null || !view.TextBuffer.ContentType.IsOfType("SQL"))
            {
                Status("SQLFinish: click in a query window first.");
                return;
            }
            Span span = ScriptSpan(view);
            string before = view.TextBuffer.CurrentSnapshot.GetText(span);
            try
            {
                string after = Formatter.Format(before);
                if (after != before) view.TextBuffer.Replace(span, after);
                Status("SQLFinish: formatted.");
            }
            catch (InvalidOperationException e) // the formatter's own safety check refused
            {
                Status(e.Message);
            }
        }

        IWpfTextView ActiveView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var manager = (IVsTextManager)GetService(typeof(SVsTextManager));
            if (manager == null || manager.GetActiveView(1, null, out IVsTextView active) != 0 || active == null) return null;
            var model = (IComponentModel)GetService(typeof(SComponentModel));
            return model?.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(active);
        }

        void Status(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (GetService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText(text);
        }
    }
}
