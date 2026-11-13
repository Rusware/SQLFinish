using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using SQLFinish.Engine;

namespace SQLFinish.Vs
{
    /// <summary>
    /// The scripts that were executed, newest first, with the selected one shown in full below.
    /// Type to filter (script text, server or database), Enter inserts at the caret, Esc cancels.
    /// </summary>
    internal sealed class HistoryWindow : DialogWindow
    {
        readonly TextBox filter = new TextBox { Margin = new Thickness(8, 8, 8, 4) };
        readonly ListBox list = new ListBox { Margin = new Thickness(8, 0, 8, 4) };
        readonly TextBox preview = new TextBox
        {
            Margin = new Thickness(8, 0, 8, 0), IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        string chosen; // the script to insert

        HistoryWindow(IEnumerable<HistoryEntry> entries, bool canInsert)
        {
            Title = "Query history";
            Width = 760;
            Height = 560;
            MinWidth = 400;
            MinHeight = 300;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(this, true); // follow the SSMS colour theme

            foreach (var e in entries)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = e.When.ToString("ddd d MMM HH:mm") + "   " + e.Server + (e.Database.Length > 0 ? " / " + e.Database : "") + "   ", Opacity = 0.6 });
                string line = Regex.Replace(e.Sql, @"\s+", " ").Trim();
                row.Children.Add(new TextBlock { Text = line.Length > 200 ? line.Substring(0, 200) : line });
                var item = new ListBoxItem { Content = row, Tag = e };
                item.MouseDoubleClick += (s, a) => Accept();
                list.Items.Add(item);
            }

            var insert = new Button { Content = "Insert", IsDefault = true, IsEnabled = canInsert, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            insert.Click += (s, e) => Accept();
            var copy = new Button { Content = "Copy", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += (s, e) =>
            {
                if (Selected == null) return;
                try { Clipboard.SetText(Selected.Sql); }
                catch (System.Runtime.InteropServices.ExternalException x) { Ssms.Log("could not copy to the clipboard", x); } // another program holds it
            };
            var cancel = new Button { Content = "Close", IsCancel = true, MinWidth = 80 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            buttons.Children.Add(insert);
            buttons.Children.Add(copy);
            buttons.Children.Add(cancel);

            var panes = new Grid();
            panes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(preview, 1);
            panes.Children.Add(list);
            panes.Children.Add(preview);

            var layout = new DockPanel();
            DockPanel.SetDock(filter, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            layout.Children.Add(filter);
            layout.Children.Add(buttons);
            layout.Children.Add(panes); // fills what is left
            Content = layout;

            filter.TextChanged += (s, e) => ApplyFilter();
            list.SelectionChanged += (s, e) => preview.Text = Selected?.Sql ?? "";
            PreviewKeyDown += OnKey;
            Loaded += (s, e) =>
            {
                filter.Focus();
                ApplyFilter();
            };
        }

        IEnumerable<ListBoxItem> Shown => list.Items.Cast<ListBoxItem>().Where(i => i.Visibility == Visibility.Visible);
        HistoryEntry Selected => (list.SelectedItem as ListBoxItem)?.Tag as HistoryEntry;

        void Accept()
        {
            if (Selected == null) return;
            chosen = Selected.Sql;
            DialogResult = true;
        }

        void ApplyFilter()
        {
            string wanted = filter.Text.Trim();
            foreach (ListBoxItem item in list.Items)
            {
                var e = (HistoryEntry)item.Tag;
                bool match = (e.Sql + "\n" + e.Server + "\n" + e.Database).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
                item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            }
            if (!(list.SelectedItem is ListBoxItem current) || current.Visibility != Visibility.Visible)
                list.SelectedItem = Shown.FirstOrDefault();
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if ((e.Key != Key.Down && e.Key != Key.Up) || preview.IsKeyboardFocusWithin) return; // arrows scroll the preview when it has the focus
            var shown = Shown.ToList();
            if (shown.Count > 0)
            {
                int at = shown.IndexOf(list.SelectedItem as ListBoxItem);
                at = Math.Max(0, Math.Min(shown.Count - 1, at + (e.Key == Key.Down ? 1 : -1)));
                list.SelectedItem = shown[at];
                list.ScrollIntoView(shown[at]);
            }
            e.Handled = true;
        }

        /// <summary>
        /// Opens the history. Insert puts the chosen script at the caret of <paramref name="view"/> (over
        /// the selection, if there is one); without a query window only Copy is offered.
        /// </summary>
        public static void Show(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var entries = History.Load(History.Folder);
            if (entries.Count == 0)
            {
                Ssms.Status("SQLFinish: no history yet. Scripts are remembered as you execute them.");
                return;
            }
            var window = new HistoryWindow(entries, view != null);
            if (window.ShowModal() == true && view != null)
                view.TextBuffer.Replace(view.Selection.StreamSelectionSpan.SnapshotSpan.Span, window.chosen);
        }
    }
}
