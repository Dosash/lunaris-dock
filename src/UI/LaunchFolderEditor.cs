using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lunaris
{
    internal sealed partial class LaunchSurface
    {
        private readonly Grid editor = new Grid { Visibility = Visibility.Collapsed };
        private Action editorSubmit;
        private bool EditingFolder { get { return editor.Visibility == Visibility.Visible; } }

        private void UpdateEditorTheme()
        {
            editor.Resources["LibraryText"] = Appearance.Text;
            editor.Resources["LibraryMuted"] = Appearance.Muted;
            editor.Resources["LibraryField"] = Appearance.Glass == null ? Appearance.Panel : Appearance.Glass.Group;
            editor.Resources["LibraryStroke"] = Appearance.Glass == null ? Appearance.Border : Appearance.Glass.Rim;
            editor.Resources["LibraryAccent"] = Appearance.Selection;
        }

        private Grid BeginEditor(string heading, string detail)
        {
            navigation.Cancel();
            layout.Visibility = Visibility.Collapsed;
            editor.Children.Clear(); editor.Visibility = Visibility.Visible; editorSubmit = null;
            var panel = new Grid { Margin = new Thickness(5,9,5,8) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition());
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var titleText = Text(heading, 23); titleText.FontFamily = UiFonts.Display; titleText.FontWeight = FontWeights.SemiBold;
            titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "LibraryText");
            Visuals.Identify(titleText, "Launchpad.Editor.Title", heading);
            panel.Children.Add(titleText);
            var detailText = Text(detail, 13); detailText.Margin = new Thickness(1,5,0,15);
            detailText.TextTrimming = TextTrimming.CharacterEllipsis; detailText.ToolTip = detail;
            detailText.SetResourceReference(TextBlock.ForegroundProperty, "LibraryMuted");
            Grid.SetRow(detailText,1); panel.Children.Add(detailText);
            editor.Children.Add(panel);
            return panel;
        }

        private void CloseEditor(bool focusSearch)
        {
            editor.Visibility = Visibility.Collapsed;
            editor.Children.Clear(); editorSubmit = null;
            layout.Visibility = Visibility.Visible;
            if (focusSearch) Keyboard.Focus(search);
        }

        private TextBox EditorInput(Grid panel, string id, string label, string initial)
        {
            var input = new TextBox { Text = initial, FontFamily = UiFonts.Text, FontSize = 15, MaxLength = 256,
                Padding = new Thickness(10,5,10,6), VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            input.SetResourceReference(Control.ForegroundProperty, "LibraryText");
            input.SetResourceReference(TextBox.CaretBrushProperty, "LibraryText");
            input.SetResourceReference(TextBox.SelectionBrushProperty, "LibraryAccent");
            Visuals.Identify(input, id, label);
            var row = new StackPanel { Margin = new Thickness(0,0,0,10) };
            var caption = Text(label, 12); caption.Margin = new Thickness(1,0,0,4);
            caption.SetResourceReference(TextBlock.ForegroundProperty, "LibraryMuted");
            row.Children.Add(caption);
            var frame = new Border { Child = input, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
            frame.SetResourceReference(Border.BackgroundProperty, "LibraryField");
            frame.SetResourceReference(Border.BorderBrushProperty, "LibraryStroke");
            row.Children.Add(frame);
            Grid.SetRow(row,2); panel.Children.Add(row);
            input.Focus(); Keyboard.Focus(input); input.SelectAll();
            return input;
        }

        private Button EditorButton(string id, string caption, bool primary, Action action)
        {
            var button = Visuals.Button(id, caption);
            button.ToolTip = null; button.MinHeight = 32; button.Margin = new Thickness(4,0,0,0);
            var label = Text(caption, 13); label.Margin = new Thickness(13,5,13,6);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.SetResourceReference(TextBlock.ForegroundProperty, "LibraryText");
            button.Content = label;
            button.SetResourceReference(Control.BackgroundProperty, primary ? "LibraryAccent" : "LibraryField");
            button.Click += delegate { action(); };
            return button;
        }

        private StackPanel Footer(Grid panel, string cancelId)
        {
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0,10,0,0) };
            footer.Children.Add(EditorButton(cancelId, Loc.Get("Cancel"), false, delegate { CloseEditor(true); }));
            Grid.SetRow(footer,4); panel.Children.Add(footer);
            return footer;
        }

        private void EditFolderName(LibraryItem previous, string parentId)
        {
            string currentId = previous == null ? null : previous.Id;
            var panel = BeginEditor(previous == null ? Loc.Get("NewFolder") : Loc.Get("RenameFolder"), FolderPath(parentId));
            var field = EditorInput(panel, "Launchpad.Folder.Name", Loc.Get("FolderName"), previous == null ? "" : previous.Name);
            var status = Text("",13); status.VerticalAlignment = VerticalAlignment.Top; status.TextWrapping = TextWrapping.Wrap;
            status.SetResourceReference(TextBlock.ForegroundProperty, "LibraryText");
            Grid.SetRow(status,3); panel.Children.Add(status);
            var footer = Footer(panel, "Launchpad.Folder.Cancel");
            editorSubmit = delegate {
                string name = field.Text.Trim();
                if (name.Length == 0) { status.Text = Loc.Get("FolderNameRequired"); Keyboard.Focus(field); return; }
                string id = currentId;
                if (previous != null && String.Equals(name, previous.Name, StringComparison.Ordinal)) { CloseEditor(true); return; }
                bool saved;
                if (previous == null) { id = library.CreateFolder(name, parentId); saved = id != null; }
                else saved = library.RenameFolder(id, name);
                if (!saved) { status.Text = Loc.Get("FolderConflict"); return; }
                ui.PersistLibrary();
                CloseEditor(false); Navigate(parentId, id);
            };
            footer.Children.Add(EditorButton("Launchpad.Folder.Save", previous == null ? Loc.Get("Create") : Loc.Get("Save"), true, editorSubmit));
            Keyboard.Focus(field);
        }

        private StackPanel PickerRows(Grid panel)
        {
            var rows = new StackPanel();
            var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            Grid.SetRow(scroll,3); panel.Children.Add(scroll);
            return rows;
        }

        private void WirePickerKeys(TextBox filter, StackPanel rows)
        {
            filter.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key != Key.Down) return;
                var first = rows.Children.OfType<Control>().FirstOrDefault(c => c.IsEnabled);
                if (first != null) { Keyboard.Focus(first); e.Handled = true; }
            };
            rows.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key != Key.Down && e.Key != Key.Up) return;
                var available = rows.Children.OfType<Control>().Where(c => c.IsEnabled).ToList();
                int at = available.FindIndex(c => c.IsKeyboardFocusWithin);
                if (at < 0) return;
                int next = at + (e.Key == Key.Down ? 1 : -1);
                if (next < 0) Keyboard.Focus(filter);
                else if (next < available.Count) { Keyboard.Focus(available[next]); available[next].BringIntoView(); }
                e.Handled = true;
            };
        }

        private void MovePicker(LibraryItem item)
        {
            var panel = BeginEditor(Loc.Get("MoveToFolder"), item.Name);
            var field = EditorInput(panel, "Launchpad.Picker.Search", Loc.Get("SearchFolders"), "");
            var rows = PickerRows(panel);
            Footer(panel, "Launchpad.Picker.Cancel");
            Action populate = delegate {
                rows.Children.Clear();
                string query = field.Text.Trim();
                var candidates = new[] { LaunchLibrary.Root }.Concat(library.Folders().Select(f => f.Id)
                    .Where(id => CanMove(item.Id, id)).OrderBy(FolderPath, StringComparer.CurrentCultureIgnoreCase));
                foreach (string id in candidates)
                {
                    string name = FolderPath(id);
                    if (name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    string destination = id;
                    var button = Visuals.Button(id == LaunchLibrary.Root ? "Launchpad.Move.Root" : "Launchpad.Move.Target", name);
                    button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.MinHeight = 34;
                    button.IsEnabled = !DesktopUi.IdEqual(item.ParentId, id);
                    var label = Text(name,14); label.Margin = new Thickness(11,4,6,4); label.TextTrimming = TextTrimming.CharacterEllipsis;
                    label.SetResourceReference(TextBlock.ForegroundProperty, "LibraryText");
                    button.Content = label; button.Opacity = button.IsEnabled ? 1 : .52;
                    button.MouseEnter += delegate { button.SetResourceReference(Control.BackgroundProperty, "LibraryAccent"); };
                    button.MouseLeave += delegate { button.Background = Brushes.Transparent; };
                    button.Click += delegate {
                        if (library.Move(item.Id, destination)) {
                            ui.PersistLibrary(); CloseEditor(true); RefreshCatalog();
                        }
                    };
                    rows.Children.Add(button);
                }
                if (rows.Children.Count == 0) rows.Children.Add(Text(Loc.Get("NoFolders"), 14));
            };
            field.TextChanged += delegate { populate(); };
            WirePickerKeys(field, rows); populate(); Keyboard.Focus(field);
        }

        private void AddApps(string destination)
        {
            var panel = BeginEditor(Loc.Get("AddApps"), FolderPath(destination));
            var field = EditorInput(panel, "Launchpad.Picker.Search", Loc.Get("SearchApp"), "");
            var rows = PickerRows(panel);
            var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var footer = Footer(panel, "Launchpad.Picker.Cancel");
            var onApply = new Action(delegate {
                if (selectedIds.Count == 0) return;
                bool changed = false;
                foreach (var app in ui.Catalog.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)) {
                    string id = LaunchLibrary.AppKey(app.Id);
                    if (selectedIds.Contains(id)) changed |= library.Move(id, destination);
                }
                if (changed) ui.PersistLibrary();
                CloseEditor(false); Navigate(destination, null);
            });
            var apply = EditorButton("Launchpad.Picker.Apply", Loc.Get("Add"), true, onApply);
            apply.IsEnabled = false; footer.Children.Add(apply);
            Action populate = delegate {
                rows.Children.Clear();
                string query = field.Text.Trim();
                foreach (var app in ui.Catalog)
                {
                    var item = library.Find(LaunchLibrary.AppKey(app.Id));
                    if (item == null || DesktopUi.IdEqual(item.ParentId, destination) ||
                        item.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    var check = new CheckBox { IsChecked = selectedIds.Contains(item.Id), FontFamily = UiFonts.Text, FontSize = 14,
                        Margin = new Thickness(7,4,7,4), Padding = new Thickness(6,0,0,1), Content = app.Name };
                    check.SetResourceReference(Control.ForegroundProperty, "LibraryText");
                    Visuals.Identify(check,"Launchpad.Picker.App",app.Name);
                    check.Checked += delegate { selectedIds.Add(item.Id); apply.IsEnabled = true; };
                    check.Unchecked += delegate { selectedIds.Remove(item.Id); apply.IsEnabled = selectedIds.Count > 0; };
                    rows.Children.Add(check);
                }
                if (rows.Children.Count == 0) rows.Children.Add(Text(query.Length == 0 ? Loc.Get("AllInFolder") : Loc.Get("NoResults"), 14));
            };
            field.TextChanged += delegate { populate(); };
            WirePickerKeys(field, rows); populate(); Keyboard.Focus(field);
        }
    }
}
