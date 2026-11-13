using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SQLFinish.Engine;

namespace SQLFinish.Vs
{
    /// <summary>
    /// A small dialog at the caret listing the columns of the statement's tables with tick boxes.
    /// Type to filter, Space ticks, Ctrl+A ticks everything shown, Enter inserts, Esc cancels.
    /// </summary>
    internal sealed class ColumnPicker : DialogWindow
    {
        readonly TextBox filter = new TextBox { Margin = new Thickness(8, 8, 8, 4) };
        readonly ListBox list = new ListBox { Margin = new Thickness(8, 0, 8, 0) };

        ColumnPicker(IEnumerable<Completion.ColumnChoice> columns)
        {
            Title = "Pick columns";
            Width = 400;
            Height = 440;
            MinWidth = 280;
            MinHeight = 220;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(this, true); // follow the SSMS colour theme

            foreach (var c in columns)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = c.Text });
                row.Children.Add(new TextBlock { Text = "   " + c.Detail, Opacity = 0.6 });
                // The tick box never takes focus: the keyboard stays in the filter box.
                list.Items.Add(new ListBoxItem { Content = new CheckBox { Content = row, Focusable = false }, Tag = c.Text });
            }

            var insert = new Button { Content = "Insert", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            insert.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            buttons.Children.Add(insert);
            buttons.Children.Add(cancel);
            var hint = new TextBlock
            {
                Text = "Type to filter.  Space ticks.  Ctrl+A ticks all shown.  Enter inserts.",
                Opacity = 0.7, Margin = new Thickness(8, 6, 8, 0), TextWrapping = TextWrapping.Wrap,
            };

            var layout = new DockPanel();
            DockPanel.SetDock(filter, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(hint, Dock.Bottom);
            layout.Children.Add(filter);
            layout.Children.Add(buttons);
            layout.Children.Add(hint);
            layout.Children.Add(list); // fills what is left
            Content = layout;

            filter.TextChanged += (s, e) => ApplyFilter();
            PreviewKeyDown += OnKey;
            Loaded += (s, e) =>
            {
                filter.Focus();
                ApplyFilter();
            };
        }

        IEnumerable<ListBoxItem> Shown => list.Items.Cast<ListBoxItem>().Where(i => i.Visibility == Visibility.Visible);
        static CheckBox Box(ListBoxItem item) => (CheckBox)item.Content;

        void ApplyFilter()
        {
            string wanted = filter.Text.Trim();
            foreach (ListBoxItem item in list.Items)
                item.Visibility = ((string)item.Tag).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!(list.SelectedItem is ListBoxItem current) || current.Visibility != Visibility.Visible)
                list.SelectedItem = Shown.FirstOrDefault();
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            var shown = Shown.ToList();
            int at = shown.IndexOf(list.SelectedItem as ListBoxItem);
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (shown.Count > 0)
                {
                    at = Math.Max(0, Math.Min(shown.Count - 1, at + (e.Key == Key.Down ? 1 : -1)));
                    list.SelectedItem = shown[at];
                    list.ScrollIntoView(shown[at]);
                }
            }
            else if (e.Key == Key.Space)
            {
                if (at >= 0) Box(shown[at]).IsChecked = Box(shown[at]).IsChecked != true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                bool tick = shown.Any(i => Box(i).IsChecked != true);
                foreach (var i in shown) Box(i).IsChecked = tick;
            }
            else return;
            e.Handled = true;
        }

        /// <summary>The ticked columns in list order; with none ticked, the highlighted one.</summary>
        List<string> Picked()
        {
            var ticked = list.Items.Cast<ListBoxItem>().Where(i => Box(i).IsChecked == true).Select(i => (string)i.Tag).ToList();
            if (ticked.Count == 0 && list.SelectedItem is ListBoxItem current && current.Visibility == Visibility.Visible) ticked.Add((string)current.Tag);
            return ticked;
        }

        /// <summary>Opens the picker at the caret of <paramref name="view"/> and inserts what was chosen.</summary>
        public static void Show(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var caret = view.Caret.Position.BufferPosition;
            string text = caret.Snapshot.GetText();
            var connection = Ssms.ActiveConnection();
            var schema = ThreadHelper.JoinableTaskFactory.Run(
                () => SchemaCache.GetAsync(connection, Completion.DatabaseAt(text, caret.Position), CancellationToken.None));
            var choices = Completion.ColumnChoices(text, caret.Position, schema);
            if (choices == null)
            {
                Ssms.Status("SQLFinish: no table columns are in scope at the caret. Name the table in FROM first.");
                return;
            }

            var picker = new ColumnPicker(choices.Columns);
            var source = PresentationSource.FromVisual(view.VisualElement);
            if (source?.CompositionTarget != null)
            {
                var onScreen = view.VisualElement.PointToScreen(new Point(view.Caret.Left - view.ViewportLeft, view.Caret.Bottom - view.ViewportTop));
                var at = source.CompositionTarget.TransformFromDevice.Transform(onScreen);
                picker.Left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(at.X, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - picker.Width));
                picker.Top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(at.Y + 4, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - picker.Height));
            }
            else picker.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            if (picker.ShowModal() != true) return;
            var picked = picker.Picked();
            if (picked.Count == 0) return;
            Ssms.InsertColumns(view, text, choices.Start, choices.Length, string.Join(", ", picked));
        }
    }
}
