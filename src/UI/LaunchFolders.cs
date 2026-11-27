using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Lunaris
{
    internal sealed partial class LaunchSurface
    {
        private const string LibraryDragFormat = "Lunaris.LibraryItem.1";
        private LaunchLibrary library;
        private string folderId = LaunchLibrary.Root;
        private readonly UIElement navigationLogo;
        private readonly Button backButton;
        private readonly TextBlock breadcrumb;
        private bool dragging;
        private bool globalQuery;
        private bool BrowsingFolder { get { return category == "All" && search.Text.Trim().Length == 0; } }

        private LibraryItem ForApp(AppEntry app)
        {
            string id = LaunchLibrary.AppKey(app.Id);
            return library.Find(id) ?? new LibraryItem { Id = id, App = app, Name = app.Name, ParentId = LaunchLibrary.Root };
        }

        private string ParentFolder()
        {
            var folder = library == null ? null : library.Find(folderId);
            return folder == null ? LaunchLibrary.Root : folder.ParentId;
        }

        private string FolderPath(string id)
        {
            var names = new Stack<string>();
            for (var node = library.Find(id); node != null; node = library.Find(node.ParentId)) names.Push(node.Name);
            return names.Count == 0 ? "Приложения" : String.Join(" › ", names);
        }

        private void UpdateFolderHeader()
        {
            bool atRoot = folderId == LaunchLibrary.Root;
            title.Text = search.Text.Trim().Length > 0 ? "Поиск" : atRoot ? "Приложения" : library.Find(folderId).Name;
            title.ToolTip = atRoot ? null : Visuals.Tip(FolderPath(folderId));
            AutomationProperties.SetName(title, title.Text);
            bool inside = !atRoot && !globalQuery;
            breadcrumb.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;
            if (inside) {
                string parent = ParentFolder();
                breadcrumb.Text = parent == LaunchLibrary.Root ? "Приложения" : "Приложения › " + FolderPath(parent);
                breadcrumb.ToolTip = Visuals.Tip(breadcrumb.Text);
            }
            navigationLogo.Visibility = atRoot ? Visibility.Visible : Visibility.Collapsed;
            backButton.Visibility = atRoot ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)backButton.Content).Foreground = Appearance.Text;
        }

        private void Navigate(string destination, string selection)
        {
            Action change = delegate {
                category = "All"; folderId = destination; selected = 0;
                clearingQuery = true;
                try { search.Clear(); } finally { clearingQuery = false; }
                BuildPills(); RefreshCatalog(selection);
            };
            if (DesktopUi.IdEqual(folderId,destination) && !globalQuery) change();
            else {
                bool leaving = !globalQuery && folderId != LaunchLibrary.Root &&
                    (selection != null || DesktopUi.IdEqual(destination, ParentFolder()));
                int index = leaving ? library.Children(destination).FindIndex(item => DesktopUi.IdEqual(item.Id, selection ?? folderId)) :
                    results.FindIndex(item => DesktopUi.IdEqual(item.Id, destination));
                var origin = new Point(.5,.5);
                if (index >= 0 && (leaving || index / Capacity == Page)) {
                    int position = index % Capacity;
                    origin = new Point((position % columns + .5) / columns, (position / columns + .5) / rows);
                }
                navigation.Change(leaving ? LaunchMove.LeaveFolder : LaunchMove.EnterFolder, origin, change);
            }
            Keyboard.Focus(search);
        }

        private void NavigateUp()
        {
            if (globalQuery) { search.Clear(); Keyboard.Focus(search); return; }
            if (folderId != LaunchLibrary.Root) Navigate(ParentFolder(), folderId);
        }

        private void OpenItem(LibraryItem item)
        {
            if (item.IsFolder) Navigate(item.Id, null);
            else ui.Launch(item.App);
        }

        private UIElement FolderArt(LibraryItem folder)
        {
            var icon = new Grid { Width = iconSize, Height = iconSize, IsHitTestVisible = false };
            string top = folder.IsSystem ? "#95DDF9" : "#ADBCFC";
            string bottom = folder.IsSystem ? "#449EDB" : "#788AD7";
            icon.Children.Add(new Border { Width = iconSize * .56, Height = iconSize * .30,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1,iconSize*.10,0,0), CornerRadius = new CornerRadius(iconSize*.09),
                Background = Visuals.Brush(bottom) });
            var preview = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(iconSize*.12,iconSize*.07,iconSize*.12,iconSize*.07) };
            foreach (var child in library.Children(folder.Id).Where(i => !i.IsFolder).Take(4))
                preview.Children.Add(Visuals.Icon(child.App, iconSize*.25));
            icon.Children.Add(new Border { Margin = new Thickness(0,iconSize*.24,0,iconSize*.07),
                Background = new LinearGradientBrush(Visuals.Brush(top).Color,Visuals.Brush(bottom).Color,90),
                BorderBrush = Visuals.Brush("#50FFFFFF"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(iconSize*.10), Child = preview });
            return icon;
        }

        private static MenuItem LibraryAction(ItemsControl menu, string id, string caption, Action action)
        {
            var item = Visuals.MenuItem(caption, action);
            Visuals.Identify(item, id, caption);
            menu.Items.Add(item);
            return item;
        }

        private void AddLibraryActions(ContextMenu menu)
        {
            if (library == null) return;
            string destination = BrowsingFolder ? folderId : LaunchLibrary.Root;
            LibraryAction(menu, "Launchpad.Action.CreateFolder", "Новая папка…", delegate { EditFolderName(null, destination); });
            if (!BrowsingFolder) return;
            if (folderId != LaunchLibrary.Root)
                LibraryAction(menu, "Launchpad.Folder.AddApps", "Добавить приложения…", delegate { AddApps(destination); });
            bool manual = library.ManualOrder(destination);
            string selectedId = results.Count > 0 ? results[selected].Id : null;
            var alpha = LibraryAction(menu, "Launchpad.Sort.Alphabetical", "По алфавиту", delegate {
                if (library.SortByName(destination)) SaveLibrary(selectedId);
            });
            var hand = LibraryAction(menu, "Launchpad.Sort.Manual", "Вручную", delegate {
                if (library.SetManualOrder(destination)) SaveLibrary(selectedId);
            });
            alpha.IsCheckable = hand.IsCheckable = true;
            alpha.IsChecked = !manual; hand.IsChecked = manual;
        }

        private void SaveLibrary(string selection)
        {
            ui.PersistLibrary(); RefreshCatalog(selection);
        }

        private void RemoveFolder(LibraryItem item)
        {
            if (!library.DeleteFolder(item.Id)) return;
            ui.PersistLibrary(); Navigate(item.ParentId, null);
        }

        private void AttachLibraryMenu(Button tile, LibraryItem item)
        {
            var menu = Visuals.Menu();
            tile.ContextMenu = menu;
            menu.Opened += delegate {
                menu.Items.Clear();
                if (item.IsFolder)
                {
                    LibraryAction(menu, "Launchpad.Folder.Open", "Открыть папку", delegate { OpenItem(item); });
                    LibraryAction(menu, "Launchpad.Folder.AddApps", "Добавить приложения…", delegate { AddApps(item.Id); });
                    if (!item.IsSystem)
                    {
                        LibraryAction(menu, "Launchpad.Folder.Rename", "Переименовать…", delegate { EditFolderName(item, item.ParentId); });
                        LibraryAction(menu, "Launchpad.Folder.Remove", "Удалить папку и вернуть содержимое", delegate { RemoveFolder(item); });
                    }
                }
                else
                {
                    LibraryAction(menu, "Launchpad.Item.Pin", ui.Pinned(item.App.Id) ? "Открепить от Dock" : "Закрепить в Dock",
                        delegate { ui.TogglePin(item.App); RefreshCatalog(); });
                }
                if (library.Find(item.Id) == null) return;
                menu.Items.Add(new Separator());
                LibraryAction(menu, "Launchpad.Item.Move", "Переместить в папку…", delegate { MovePicker(item); });
                if (!BrowsingFolder) return;
                int index = results.FindIndex(node => DesktopUi.IdEqual(node.Id, item.Id));
                var earlier = LibraryAction(menu, "Launchpad.Item.Earlier", "Переместить раньше", delegate { ShiftItem(item, -1); });
                var later = LibraryAction(menu, "Launchpad.Item.Later", "Переместить позже", delegate { ShiftItem(item, 1); });
                earlier.IsEnabled = index > 0;
                later.IsEnabled = index >= 0 && index < results.Count - 1;
            };
        }

        private void ShiftItem(LibraryItem item, int direction)
        {
            var siblings = library.Children(item.ParentId);
            int index = siblings.FindIndex(node => DesktopUi.IdEqual(node.Id, item.Id));
            int target = index + direction;
            if (index < 0 || target < 0 || target >= siblings.Count) return;
            int before = direction < 0 ? target : target + 1;
            if (library.Move(item.Id, item.ParentId, before >= siblings.Count ? null : siblings[before].Id)) SaveLibrary(item.Id);
        }

        private bool HandleLibraryKey(KeyEventArgs e)
        {
            var item = results.Count == 0 ? null : results[selected];
            var modifiers = Keyboard.Modifiers;
            if (e.Key == Key.N && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                EditFolderName(null, BrowsingFolder ? folderId : LaunchLibrary.Root);
            else if ((e.Key == Key.System && e.SystemKey == Key.Left && modifiers == ModifierKeys.Alt ||
                      e.Key == Key.Back && search.Text.Length == 0) && folderId != LaunchLibrary.Root)
                NavigateUp();
            else if (e.Key == Key.F2 && item != null && item.IsFolder && !item.IsSystem)
                EditFolderName(item, item.ParentId);
            else if (e.Key == Key.F10 && modifiers == ModifierKeys.Shift && item != null) {
                var tile = grid.Children[selected % Capacity] as Button;
                if (tile != null) { tile.ContextMenu.PlacementTarget = tile; tile.ContextMenu.IsOpen = true; }
            }
            else if ((e.Key == Key.Left || e.Key == Key.Right) && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) &&
                     BrowsingFolder && item != null) ShiftItem(item, e.Key == Key.Left ? -1 : 1);
            else return false;
            e.Handled = true;
            return true;
        }

        private bool CanMove(string id, string destination)
        {
            var source = library.Find(id);
            if (source == null || destination == null) return false;
            if (destination != LaunchLibrary.Root && library.Find(destination) == null) return false;
            for (var parent = library.Find(destination); parent != null; parent = library.Find(parent.ParentId))
                if (DesktopUi.IdEqual(source.Id, parent.Id)) return false;
            return true;
        }

        private string DragId(DragEventArgs e)
        {
            return dragging && e.Data.GetDataPresent(LibraryDragFormat, false) ? e.Data.GetData(LibraryDragFormat, false) as string : null;
        }

        private void DragOverFolder(DragEventArgs e, string destination)
        {
            e.Effects = BrowsingFolder && CanMove(DragId(e), destination) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropInto(DragEventArgs e, string destination)
        {
            string id = DragId(e);
            if (BrowsingFolder && CanMove(id, destination) && library.Move(id, destination)) SaveLibrary(id);
            e.Handled = true;
        }

        private void AttachLibraryDrag(Button tile, LibraryItem item)
        {
            if (!BrowsingFolder) return;
            Point down = new Point(); bool armed = false;
            tile.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                down = e.GetPosition(tile); armed = true;
            };
            tile.PreviewMouseLeftButtonUp += delegate { armed = false; };
            tile.PreviewMouseMove += delegate(object sender, MouseEventArgs e) {
                if (!armed || dragging || e.LeftButton != MouseButtonState.Pressed) return;
                Point current = e.GetPosition(tile);
                if (Math.Abs(current.X - down.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                armed = false; dragging = true; Mouse.Capture(null);
                try { DragDrop.DoDragDrop(tile, new DataObject(LibraryDragFormat, item.Id), DragDropEffects.Move); }
                finally { dragging = false; }
            };
            tile.AllowDrop = true;
            tile.DragOver += delegate(object sender, DragEventArgs e) {
                bool into = OverFolderCenter(tile, item, e);
                DragOverFolder(e, into ? item.Id : item.ParentId);
                tile.Opacity = e.Effects == DragDropEffects.Move ? .66 : 1;
            };
            tile.DragLeave += delegate { tile.Opacity = 1; };
            tile.Drop += delegate(object sender, DragEventArgs e) {
                tile.Opacity = 1;
                if (OverFolderCenter(tile, item, e)) { DropInto(e, item.Id); return; }
                string id = DragId(e);
                if (id != null && !DesktopUi.IdEqual(id, item.Id)) {
                    var siblings = library.Children(item.ParentId);
                    int index = siblings.FindIndex(node => DesktopUi.IdEqual(node.Id, item.Id));
                    if (index >= 0) {
                        if (e.GetPosition(tile).X > tile.ActualWidth / 2) index++;
                        string before = index < siblings.Count ? siblings[index].Id : null;
                        if (CanMove(id, item.ParentId) && library.Move(id, item.ParentId, before)) SaveLibrary(id);
                    }
                }
                e.Handled = true;
            };
        }

        private static bool OverFolderCenter(Button tile, LibraryItem item, DragEventArgs e)
        {
            double x = e.GetPosition(tile).X;
            return item.IsFolder && x >= tile.ActualWidth * .2 && x <= tile.ActualWidth * .8;
        }
    }
}
