using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Lunaris
{
    internal sealed partial class LaunchSurface
    {
        private static readonly string[] Utilities = { "calculator", "calc.exe", "калькулятор", "clock", "часы", "терминал",
            "terminal", "cmd", "powershell", "параметры", "settings", "notepad", "блокнот", "snipping", "ножницы",
            "taskmgr", "диспетчер задач", "charmap", "таблица символов", "regedit", "проводник", "explorer",
            "быстрая помощь", "quick assist", "security", "безопасность", "control panel", "панель управления" };
        private static readonly string[] Media = { "photo", "фотограф", "paint", "camera", "камера", "music",
            "музыка", "player", "плеер", "проигрыватель", "video", "видео", "clipchamp", "spotify",
            "подкаст", "podcast", "recorder", "звукозапись", "vlc", "photoshop", "lightroom" };

        private void BuildPills()
        {
            pills.Children.Clear();
            AddPill("All","Все");
            AddPill("Pinned","Закреплённые");
            AddPill("Open","Открытые");
            AddPill("Utilities","Утилиты");
            AddPill("Media","Мультимедиа");
        }

        private void AddPill(string id,string name)
        {
            bool active = category == id && !globalQuery;
            var pill = Visuals.Button("Launchpad.Category."+id,name);
            pill.ToolTip = null;
            pill.Margin = new Thickness(0,0,5,4);
            var text = Text(name,12);
            text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            text.Foreground = active ? Appearance.Text : Appearance.Muted;
            pill.Content = new Border { Child = text, Padding = new Thickness(10,3,10,4),
                CornerRadius = new CornerRadius(11), Background = active ?
                Translucent(Appearance.Selection,240) : Translucent(Appearance.Panel,138),
                BorderThickness = new Thickness(1), BorderBrush = active ?
                Translucent(Appearance.Accent,125) : Translucent(Appearance.Muted,35) };
            AutomationProperties.SetItemStatus(pill,active ? "Выбрано" : "");
            pill.Click += delegate {
                if (id == "All" && folderId != LaunchLibrary.Root && !globalQuery) {
                    string child = folderId;
                    for (var node = library.Find(child); node != null && node.ParentId != LaunchLibrary.Root; node = library.Find(child))
                        child = node.ParentId;
                    Navigate(LaunchLibrary.Root, child);
                    return;
                }
                category = id; folderId = LaunchLibrary.Root; selected = 0; search.Clear(); BuildPills(); RefreshCatalog(); Keyboard.Focus(search);
            };
            pills.Children.Add(pill);
        }

        private static bool Matches(AppEntry app,string[] terms)
        {
            string text = app.Name + " " + app.Id;
            return terms.Any(term => text.IndexOf(term,StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal void RefreshCatalog()
        {
            RefreshCatalog(null);
        }

        private void RefreshCatalog(string reveal)
        {
            navigation.Cancel();
            wheelRemainder = 0;
            var words = search.Text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (globalQuery != (words.Length > 0)) { globalQuery = words.Length > 0; BuildPills(); }
            var pins = ui.Settings.Pins.Select(ui.FindApp).Where(app => app != null).ToList();
            library = new LaunchLibrary(ui.Settings, ui.Catalog.Concat(pins));
            if (folderId != LaunchLibrary.Root && library.Find(folderId) == null) folderId = LaunchLibrary.Root;
            IEnumerable<LibraryItem> items;
            if (words.Length > 0)
            {
                var running = ui.Windows.Where(window => window.App != null).Select(window => window.App);
                var apps = ui.Catalog.Concat(pins).Concat(running).Select(ForApp);
                items = library.Folders().Concat(apps)
                    .Where(item => words.All(word => item.Name.IndexOf(word, StringComparison.CurrentCultureIgnoreCase) >= 0))
                    .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
            }
            else
            {
                switch (category)
                {
                    case "Pinned": items = pins.Select(ForApp); break;
                    case "Open": items = ui.Windows.Where(w => w.App != null).Select(w => ForApp(w.App)); break;
                    case "Utilities": items = ui.Catalog.Where(app => Matches(app, Utilities)).Select(ForApp); break;
                    case "Media": items = ui.Catalog.Where(app => Matches(app, Media)).Select(ForApp); break;
                    default: items = library.Children(folderId); break;
                }
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            results.Clear();
            results.AddRange(items.Where(item => seen.Add(item.Id)));
            UpdateFolderHeader();
            if (reveal != null) {
                int index = results.FindIndex(item => DesktopUi.IdEqual(item.Id, reveal));
                if (index >= 0) selected = index;
            }
            selected = Math.Max(0,Math.Min(selected,results.Count-1));
            placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderPage();
        }

        private void RenderPage()
        {
            grid.Children.Clear(); grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear(); frames.Clear();
            for (int c=0;c<columns;c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r=0;r<rows;r++) grid.RowDefinitions.Add(new RowDefinition());
            int start = Page * Capacity;
            for (int i=0;i<Math.Min(Capacity,results.Count-start);i++) {
                var item = results[start+i];
                var tile = Visuals.Button(item.IsFolder ? "Launchpad.Folder" : "Launchpad.App", item.Name);
                tile.Focusable = false; tile.Margin = new Thickness(2,1,2,1);
                var body = new StackPanel { HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
                var art = new Grid { Width=iconSize+4,Height=iconSize+2 };
                art.Children.Add(item.IsFolder ? FolderArt(item) : Visuals.Icon(item.App, iconSize));
                if (!item.IsFolder && ui.Pinned(item.App.Id)) {
                    var mark = Glyph("\uE718",8); mark.Foreground=Appearance.Muted;
                    art.Children.Add(new Border { Background=Appearance.Panel,BorderBrush=Translucent(Appearance.Muted,120),
                        BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Width=15,Height=15,
                        VerticalAlignment=VerticalAlignment.Bottom,HorizontalAlignment=HorizontalAlignment.Right,Child=mark });
                    AutomationProperties.SetItemStatus(tile,"Закреплено");
                }
                body.Children.Add(art);
                var name = Text(item.Name,12.5); name.Width = Math.Max(30,cellWidth-12); name.Height=32;
                name.TextAlignment=TextAlignment.Center; name.Margin=new Thickness(0,5,0,0);
                name.TextWrapping=TextWrapping.Wrap; name.TextTrimming=TextTrimming.CharacterEllipsis;
                name.LineStackingStrategy=LineStackingStrategy.BlockLineHeight; name.LineHeight=15;
                body.Children.Add(name);
                var frame = new Border { CornerRadius=new CornerRadius(12), BorderThickness=new Thickness(1), Child=body,
                    VerticalAlignment=VerticalAlignment.Stretch,HorizontalAlignment=HorizontalAlignment.Stretch };
                int index = start+i;
                tile.Content=frame;
                tile.MouseMove += delegate { if (!navigation.Active && selected!=index) { selected=index; MarkSelection(); } };
                tile.Click += delegate { if (!dragging) OpenItem(item); };
                if (item.IsFolder) AutomationProperties.SetItemStatus(tile, item.IsSystem ? "Папка Windows" : "Личная папка");
                if (search.Text.Trim().Length != 0 && item.ParentId != LaunchLibrary.Root)
                    tile.ToolTip = Visuals.Tip(item.Name + "\n" + FolderPath(item.ParentId));
                AttachLibraryMenu(tile, item);
                AttachLibraryDrag(tile, item);
                Grid.SetRow(tile,i/columns); Grid.SetColumn(tile,i%columns);
                frames.Add(frame); grid.Children.Add(tile);
            }
            empty.Visibility = results.Count==0 ? Visibility.Visible : Visibility.Collapsed;
            findFiles.Visibility = results.Count == 0 && search.Text.Trim().Length != 0 ? Visibility.Visible : Visibility.Collapsed;
            addToEmptyFolder.Visibility = results.Count == 0 && BrowsingFolder && folderId != LaunchLibrary.Root &&
                !ui.CatalogLoading ? Visibility.Visible : Visibility.Collapsed;
            if (results.Count==0)
                empty.Text = ui.CatalogLoading ? "Загрузка приложений…" : ui.CatalogError!=null ?
                    "Не удалось загрузить приложения" : search.Text.Trim().Length>0 ? "Ничего не найдено" :
                    category=="Pinned" ? "Закреплённых приложений пока нет" :
                    category=="Open" ? "Нет открытых приложений" : folderId != LaunchLibrary.Root ? "Папка пуста" : "Здесь пока нет приложений";
            MarkSelection(); RenderPager();
        }

        private void MarkSelection()
        {
            for(int i=0;i<frames.Count;i++) {
                bool chosen=i==selected%Capacity;
                frames[i].Background=chosen ? Translucent(Appearance.Selection,188) : Brushes.Transparent;
                frames[i].BorderBrush=chosen ? Translucent(Appearance.Accent,105) : Brushes.Transparent;
            }
        }

        private void RenderPager()
        {
            pager.Children.Clear();
            if(Pages<=1)return;
            PageArrow(-1);
            if(Pages<=9) {
                for(int i=0;i<Pages;i++) {
                    int page=i;
                    var b=Visuals.Button("Launchpad.Page."+(i+1),"Страница "+(i+1)+" из "+Pages);
                    b.Width=17; b.Focusable=false;
                    b.Content=new Ellipse { Fill=i==Page ? Appearance.Text : Translucent(Appearance.Muted,125),
                        Width=i==Page ? 7:5, Height=i==Page ? 7:5, HorizontalAlignment=HorizontalAlignment.Center,
                        VerticalAlignment=VerticalAlignment.Center };
                    b.Click+=delegate { ChangePage(page-Page); }; pager.Children.Add(b);
                }
            } else {
                var text=Text((Page+1)+" / "+Pages,12); text.Margin=new Thickness(6,0,6,0); pager.Children.Add(text);
            }
            PageArrow(1);
        }

        private void PageArrow(int step)
        {
            var b=Visuals.Button(step>0?"Launchpad.Next":"Launchpad.Previous",step>0?"Следующая страница":"Предыдущая страница");
            b.Width=26; b.Focusable=false; b.IsEnabled=Page+step>=0 && Page+step<Pages; b.Opacity=b.IsEnabled ? .85:.30;
            b.Content=Glyph(step>0?"\uE76C":"\uE76B",11); b.Click+=delegate { ChangePage(step); }; pager.Children.Add(b);
        }

        private void ChangePage(int delta)
        {
            int page=Math.Max(0,Math.Min(Page+delta,Pages-1));
            if(page!=Page) ChangeSelection(page*Capacity);
        }

        private void ChangeSelection(int index)
        {
            index = Math.Max(0, Math.Min(results.Count-1, index));
            int targetPage = index / Capacity;
            if (targetPage == Page) {
                selected = index; MarkSelection(); return;
            }
            var move = targetPage > Page ? LaunchMove.NextPage : LaunchMove.PreviousPage;
            navigation.Change(move, new Point(.5,.5), delegate { selected = index; RenderPage(); });
        }

        private void OnKey(object sender,KeyEventArgs e)
        {
            if (EditingFolder) {
                if (e.Key == Key.Escape) { CloseEditor(true); e.Handled = true; }
                else if (e.Key == Key.Enter && !(Keyboard.FocusedElement is Button) && editorSubmit != null) {
                    editorSubmit(); e.Handled = true;
                }
                return;
            }
            if (HandleLibraryKey(e)) return;
            int offset=0;
            switch(e.Key) {
                case Key.Escape: Dismiss(); break;
                case Key.Enter:
                    if(Keyboard.FocusedElement is Button)return;
                    if(results.Count>0)OpenItem(results[selected]); break;
                case Key.Left: offset=-1; break;
                case Key.Right: offset=1; break;
                case Key.Up: offset=-columns; break;
                case Key.Down: offset=columns; break;
                case Key.PageUp: ChangePage(-1); break;
                case Key.PageDown: ChangePage(1); break;
                case Key.Home:
                case Key.End:
                    if((Keyboard.Modifiers & ModifierKeys.Control)==0)return;
                    ChangeSelection(e.Key==Key.Home ? 0 : results.Count-1); break;
                case Key.Apps:
                    if(results.Count>0) {
                        var tile=(Button)grid.Children[selected%Capacity];
                        tile.ContextMenu.PlacementTarget=tile; tile.ContextMenu.IsOpen=true;
                    } break;
                case Key.F:
                    if((Keyboard.Modifiers & ModifierKeys.Control)==0)return;
                    Keyboard.Focus(search); search.SelectAll(); break;
                case Key.OemComma:
                    if((Keyboard.Modifiers & ModifierKeys.Control)==0)return;
                    ui.OpenPreferences(); break;
                default:return;
            }
            if(offset!=0 && results.Count>0) ChangeSelection(selected+offset);
            e.Handled=true;
        }
    }
}
