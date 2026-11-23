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
    /// The scripts that were executed, pinned ones first and then newest first, with the selected one
    /// shown in full below. Type to filter (script text, server or database) or pick a server; Enter
    /// inserts at the caret, Esc cancels. Scripts can be pinned, deleted, or cleared out together.
    /// </summary>
    internal sealed class HistoryWindow : DialogWindow
    {
        const string Everywhere = "All servers";
        readonly List<HistoryEntry> entries;
        readonly TextBox filter = new TextBox { Margin = new Thickness(8, 8, 8, 4) };
        readonly ComboBox place = new ComboBox { Margin = new Thickness(0, 8, 8, 4), MinWidth = 200 };
        readonly ListBox list = new ListBox { Margin = new Thickness(8, 0, 8, 4) };
        readonly Button pin = new Button { Content = "Pin", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        readonly TextBox preview = new TextBox
        {
            Margin = new Thickness(8, 0, 8, 0), IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        string chosen; // the script to insert

        HistoryWindow(List<HistoryEntry> entries, bool canInsert)
        {
            this.entries = entries;
            Title = "Query history";
            Width = 760;
            Height = 560;
            MinWidth = 560;
            MinHeight = 300;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(this, true); // follow the SSMS colour theme

            Func<string, Action, Button> button = (text, click) =>
            {
                var b = new Button { Content = text, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
                b.Click += (s, e) => click();
                return b;
            };
            var insert = button("Insert", Accept);
            insert.IsDefault = true;
            insert.IsEnabled = canInsert;
            var copy = button("Copy", () =>
            {
                if (Selected == null) return;
                try { Clipboard.SetText(Selected.Sql); }
                catch (System.Runtime.InteropServices.ExternalException x) { Ssms.Log("could not copy to the clipboard", x); } // another program holds it
            });
            var cancel = new Button { Content = "Close", IsCancel = true, MinWidth = 80 };
            pin.Click += (s, e) => TogglePin();
            pin.ToolTip = "A pinned script is listed first and is never removed to make room for newer ones.";

            var manage = new StackPanel { Orientation = Orientation.Horizontal };
            manage.Children.Add(pin);
            manage.Children.Add(button("Delete", DeleteSelected));
            manage.Children.Add(button("Clear...", Clear));
            var use = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            use.Children.Add(insert);
            use.Children.Add(copy);
            use.Children.Add(cancel);
            var buttons = new DockPanel { Margin = new Thickness(8) };
            DockPanel.SetDock(manage, Dock.Left);
            buttons.Children.Add(manage);
            buttons.Children.Add(use);

            var filters = new DockPanel();
            DockPanel.SetDock(place, Dock.Right);
            filters.Children.Add(place);
            filters.Children.Add(filter);

            var panes = new Grid();
            panes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(preview, 1);
            panes.Children.Add(list);
            panes.Children.Add(preview);

            var layout = new DockPanel();
            DockPanel.SetDock(filters, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            layout.Children.Add(filters);
            layout.Children.Add(buttons);
            layout.Children.Add(panes); // fills what is left
            Content = layout;

            filter.TextChanged += (s, e) => ApplyFilter();
            place.SelectionChanged += (s, e) => ApplyFilter();
            list.SelectionChanged += (s, e) =>
            {
                preview.Text = Selected?.Sql ?? "";
                pin.Content = Selected?.Pinned == true ? "Unpin" : "Pin";
            };
            PreviewKeyDown += OnKey;
            Loaded += (s, e) =>
            {
                filter.Focus();
                Fill();
            };
        }

        IEnumerable<ListBoxItem> Shown => list.Items.Cast<ListBoxItem>().Where(i => i.Visibility == Visibility.Visible);
        HistoryEntry Selected => (list.SelectedItem as ListBoxItem)?.Tag as HistoryEntry;

        /// <summary>The server and database a script ran on, as listed in the server filter.</summary>
        static string Place(HistoryEntry e) => e.Server.Length == 0 ? "(not connected)" : e.Server + (e.Database.Length > 0 ? " / " + e.Database : "");

        /// <summary>Rebuilds the list and the server filter from <see cref="entries"/>, keeping the selection where it can.</summary>
        void Fill()
        {
            var selected = Selected;
            string where = place.SelectedItem as string ?? Everywhere;
            var places = new[] { Everywhere }.Concat(entries.Select(Place).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)).ToList();
            place.ItemsSource = places;
            place.SelectedItem = places.Contains(where) ? where : Everywhere;

            list.Items.Clear();
            foreach (var e in entries.OrderByDescending(x => x.Pinned).ThenByDescending(x => x.When))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = (e.Pinned ? "★ " : "") + e.When.ToString("ddd d MMM HH:mm") + "   " + Place(e) + "   ", Opacity = 0.6 });
                string line = Regex.Replace(e.Sql, @"\s+", " ").Trim();
                row.Children.Add(new TextBlock { Text = line.Length > 200 ? line.Substring(0, 200) : line });
                var item = new ListBoxItem { Content = row, Tag = e };
                item.MouseDoubleClick += (s, a) => Accept();
                list.Items.Add(item);
                if (e == selected) list.SelectedItem = item;
            }
            ApplyFilter();
        }

        void Accept()
        {
            if (Selected == null) return;
            chosen = Selected.Sql;
            DialogResult = true;
        }

        void ApplyFilter()
        {
            string wanted = filter.Text.Trim(), where = place.SelectedItem as string ?? Everywhere;
            foreach (ListBoxItem item in list.Items)
            {
                var e = (HistoryEntry)item.Tag;
                bool match = (where == Everywhere || Place(e) == where)
                    && (e.Sql + "\n" + e.Server + "\n" + e.Database).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
                item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            }
            if (!(list.SelectedItem is ListBoxItem current) || current.Visibility != Visibility.Visible)
                list.SelectedItem = Shown.FirstOrDefault();
        }

        /// <summary>Runs a change to the history files. False, with the reason logged, when a file could not be changed.</summary>
        static bool Change(Action change, string what)
        {
            try
            {
                change();
                return true;
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                Ssms.Log("could not " + what, e);
                return false;
            }
        }

        void TogglePin()
        {
            var e = Selected;
            if (e != null && Change(() => History.SetPinned(e, !e.Pinned), "pin the script")) Fill();
        }

        void DeleteSelected()
        {
            var e = Selected;
            if (e == null) return;
            int at = Shown.ToList().IndexOf((ListBoxItem)list.SelectedItem);
            if (!Change(() => History.Delete(e), "delete the script")) return;
            entries.Remove(e);
            Fill();
            var shown = Shown.ToList();
            if (shown.Count > 0) list.SelectedItem = shown[Math.Min(at, shown.Count - 1)]; // the one that moved up into its place
        }

        /// <summary>Deletes every script the filters are showing, except the pinned ones.</summary>
        void Clear()
        {
            var doomed = Shown.Select(i => (HistoryEntry)i.Tag).Where(e => !e.Pinned).ToList();
            if (doomed.Count == 0) return;
            string which = doomed.Count == entries.Count ? "all " + doomed.Count.ToString("N0") + " scripts" : "the " + doomed.Count.ToString("N0") + " scripts listed that are not pinned";
            var answer = MessageBox.Show(this, "Delete " + which + " from the history?\n\nThis cannot be undone.", "Query history",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            Change(() =>
            {
                foreach (var e in doomed)
                {
                    History.Delete(e);
                    entries.Remove(e);
                }
            }, "clear the history");
            Fill();
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && list.IsKeyboardFocusWithin) // in the filter box Delete edits the text
            {
                DeleteSelected();
                e.Handled = true;
                return;
            }
            if ((e.Key != Key.Down && e.Key != Key.Up) || preview.IsKeyboardFocusWithin || place.IsKeyboardFocusWithin) return; // arrows scroll the preview and step through the servers when those have the focus
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
