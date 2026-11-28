using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lunaris
{
    internal sealed class StatusBarWindow : Window
    {
        private readonly DesktopUi ui;
        private ScreenInfo screen;
        private readonly Border frame = new Border { BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(9,0,9,0) };
        private readonly DockPanel row = new DockPanel();
        private readonly StackPanel apps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly ScrollViewer scroller;
        private readonly TextBlock trayMessage = new TextBlock { FontSize = 12, MaxWidth = 210, Margin = new Thickness(6,0,6,0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button clock, notifications, network, sound, battery, tray, inputLanguage;
        private readonly StatusMenus menus;
        private readonly SteamTrayMenu steamMenu = new SteamTrayMenu();
        private readonly Dictionary<string,Button> trayButtons = new Dictionary<string,Button>();
        private const double TrayIconSize = 18;
        private readonly List<Button> actions = new List<Button>();
        private StatusBarPosition position;
        private Brush palette;
        private LiquidGlassMaterial glass;
        private string language;
        private DateTime nextStatus;
        private SystemStatus status = new SystemStatus();
        private bool readingStatus, closed, expanded, readingTray, holdingTray;
        private static readonly FontFamily Symbols = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        internal double PhysicalBottom { get { return IsVisible && position != null ? position.Bottom : screen.Bounds.Top; } }

        internal StatusBarWindow(DesktopUi ui, ScreenInfo screen)
        {
            this.ui = ui; this.screen = screen;
            menus = new StatusMenus(this);
            Title = Loc.Get("ShowStatusBar"); FontFamily = UiFonts.Text;
            Visuals.Identify(this,"StatusBar",Title);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = Height = 1; UseLayoutRounding = true; SnapsToDevicePixels = true;
            SourceInitialized += delegate { Visuals.ToolWindow(this,true); };
            clock = ActionButton("Clock","StatusClock","",Double.NaN,delegate { OpenSystem('N'); });
            clock.Padding = new Thickness(5,0,5,0);
            ((TextBlock)clock.Content).FontFamily = UiFonts.Text;
            ((TextBlock)clock.Content).FontSize = 13; ((TextBlock)clock.Content).FontWeight = FontWeights.SemiBold;
            notifications = ActionButton("Notifications","StatusNotifications","\uEA8F",29,delegate { OpenSystem('N'); });
            battery = ActionButton("Battery","StatusBattery","\uE83F",29,delegate { OpenSystem('A'); });
            sound = ActionButton("Sound","StatusSound","\uE767",29,delegate { menus.OpenSound(sound); });
            network = ActionButton("Network","StatusNetwork","\uE701",29,delegate { menus.OpenNetwork(network); });
            inputLanguage = ActionButton("Language","StatusLanguage","",Double.NaN,delegate { menus.OpenLanguage(inputLanguage); });
            inputLanguage.Padding = new Thickness(5,0,5,0); inputLanguage.MinWidth = 38;
            ((TextBlock)inputLanguage.Content).FontFamily = UiFonts.Text;
            ((TextBlock)inputLanguage.Content).FontSize = 13;
            tray = ActionButton("Tray","StatusApps","\uE76B",28,delegate { SetExpanded(!expanded); });
            foreach (Button b in new[]{clock,notifications,battery,sound,network,inputLanguage,tray}) {
                DockPanel.SetDock(b,Dock.Right); row.Children.Add(b);
            }
            Visuals.Identify(apps,"StatusBar.TrayPanel",Loc.Get("StatusApps"));
            scroller = new ScrollViewer { Content = apps, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
            scroller.PreviewMouseWheel += (s,e) => { scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset-e.Delta/3.0); e.Handled=true; };
            row.Children.Add(scroller); frame.Child = row; Content = frame;
            Closing += delegate {
                closed = true; steamMenu.Cancel(); UpdateTrayHost(); menus.Close();
                if (position != null) { position.Dispose(); position = null; }
            };
            ApplyTheme();
        }

        private Button ActionButton(string id, string key, string symbol, double width, Action action)
        {
            var button = Visuals.Button("StatusBar."+id,Loc.Get(key));
            button.Tag = key; button.Width = width; button.MinWidth = 24; button.Focusable = false;
            button.Content = new TextBlock { Text = symbol, FontFamily = Symbols, FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            button.Click += delegate { action(); };
            button.MouseEnter += delegate { button.Background = Appearance.Selection; };
            button.MouseLeave += delegate { button.Background = Brushes.Transparent; };
            actions.Add(button);
            return button;
        }

        internal void Update(ScreenInfo next, bool fullscreen)
        {
            screen = next;
            if (position == null) position = new StatusBarPosition(this,screen,ui.Platform.Preview);
            else position.Update(screen);
            UpdateTrayHost();
            if (fullscreen) {
                if (IsVisible) { SetExpanded(false); menus.Close(); Hide(); }
                return;
            }
            if (palette != Appearance.Surface || glass != Appearance.Glass || language != Loc.Language) ApplyTheme();
            if (!IsVisible) Show();
            menus.Update();
            ((TextBlock)inputLanguage.Content).Text = menus.LanguageLabel;
            PaintClock();
            if (expanded) RefreshTray();
            if (!readingStatus && DateTime.UtcNow >= nextStatus) ReadStatus();
        }

        private void ApplyTheme()
        {
            palette = Appearance.Surface; glass = Appearance.Glass; language = Loc.Language;
            frame.Background = glass == null ? palette : glass.Pane;
            frame.BorderBrush = glass == null ? Appearance.Border : glass.Rim;
            Title = Loc.Get("ShowStatusBar"); AutomationProperties.SetName(this,Title);
            Foreground = Appearance.Text;
            AutomationProperties.SetName(apps,Loc.Get("StatusApps"));
            foreach (var button in actions) {
                button.Foreground = Appearance.Text;
                SetHint(button,Loc.Get((string)button.Tag));
            }
            foreach (var button in apps.Children.OfType<Button>()) button.Foreground = Appearance.Text;
            SetExpansionHint();
            PaintStatus();
        }
        private static void SetHint(Button button,string hint)
        {
            if (AutomationProperties.GetName(button) == hint) return;
            AutomationProperties.SetName(button,hint);
            button.ToolTip = Visuals.Tip(hint);
        }
        private void PaintClock()
        {
            var now = DateTime.Now;
            string time = now.ToString("t",CultureInfo.CurrentCulture);
            ((TextBlock)clock.Content).Text = time;
            SetHint(clock,now.ToString("D",CultureInfo.CurrentCulture) + "  " + time);
        }
        private async void ReadStatus()
        {
            readingStatus = true;
            try { status = await Task.Run(new Func<SystemStatus>(SystemStatus.Read)); if (!closed) PaintStatus(); }
            catch (Exception ex) {
                if (!(ex is COMException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException ||
                    ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is InvalidCastException)) throw;
            }
            finally { readingStatus = false; nextStatus = DateTime.UtcNow.AddSeconds(5); }
        }
        private void PaintStatus()
        {
            PaintClock();
            ((TextBlock)network.Content).Text = !status.Connected ? "\uEB55" : status.Wireless ? "\uE701" : "\uE839";
            SetHint(network,Loc.Get(!status.Connected ? "StatusOffline" : status.Wireless ? "StatusWifi" : "StatusConnected"));
            ((TextBlock)sound.Content).Text = status.Muted || status.Volume == 0 ? "\uE74F" : "\uE767";
            SetHint(sound,status.Muted ? Loc.Get("StatusMuted") : Loc.Get("StatusSound") +
                (status.Volume < 0 ? "" : " · " + status.Volume + "%"));
            battery.Visibility = status.Battery < 0 ? Visibility.Collapsed : Visibility.Visible;
            if (status.Battery >= 0) {
                ((TextBlock)battery.Content).Text = Char.ConvertFromUtf32((status.OnAC ? 0xE85A : 0xE850) + Math.Min(9,status.Battery / 10));
                SetHint(battery,Loc.Get("StatusBattery") + " · " + status.Battery + "%");
            }
        }

        private void SetExpansionHint()
        {
            AutomationProperties.SetItemStatus(tray,Loc.Get(expanded ? "StatusExpanded" : "StatusCollapsed"));
        }

        private void SetExpanded(bool show)
        {
            if (expanded == show) return;
            menus.Close();
            expanded = show;
            UpdateTrayHost();
            scroller.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)tray.Content).Text = show ? "\uE76C" : "\uE76B";
            SetExpansionHint();
            if (show) { NotificationArea.RefreshOpenStrip(); RefreshTray(); }
            else { steamMenu.Cancel(); apps.Children.Clear(); trayButtons.Clear(); }
        }

        private void UpdateTrayHost()
        {
            // A replacement taskbar must receive registrations even while its strip is collapsed.
            bool needed = !closed && (expanded || (!ui.Platform.Preview && ui.Settings.ReplaceTaskbar));
            if (needed == holdingTray) return;
            holdingTray = needed;
            if (needed) NotificationArea.Open(); else NotificationArea.Close();
        }

        private void OpenSystem(char key)
        {
            menus.Close();
            if (!WinKey.ShellShortcut(key)) SetHint(key == 'N' ? notifications : battery,Loc.Get("StatusShortcutBusy"));
        }

        private async void RefreshTray()
        {
            if (readingTray) return;
            readingTray = true;
            try {
                int pixels = (int)Math.Round(TrayIconSize * screen.Scale);
                var items = await Task.Run(() => NotificationArea.Read(pixels));
                if (expanded && !closed) Populate(items);
            } catch (System.ComponentModel.Win32Exception) {
                if (expanded && !closed) TrayMessage("StatusTrayUnavailable");
            } finally { readingTray = false; }
        }

        private void TrayMessage(string key)
        {
            trayMessage.Text = Loc.Get(key);
            if (!apps.Children.Contains(trayMessage)) apps.Children.Add(trayMessage);
        }

        private void InvokeItem(Button button, bool context)
        {
            menus.Close();
            Point point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            if (button.IsMouseOver) point = button.PointToScreen(System.Windows.Input.Mouse.GetPosition(button));
            var item = (NotificationArea.Item)button.Tag;
            Point p = button.PointToScreen(new Point());
            Point q = button.PointToScreen(new Point(button.ActualWidth,button.ActualHeight));
            var bounds = new Int32Rect((int)Math.Round(p.X), (int)Math.Round(p.Y),
                (int)Math.Round(q.X) - (int)Math.Round(p.X), (int)Math.Round(q.Y) - (int)Math.Round(p.Y));
            steamMenu.Watch(item.ProcessId, bounds, screen.WorkArea, Math.Round(point.X));
            if (!NotificationArea.Activate(item, context, (int)Math.Round(point.X), (int)Math.Round(point.Y), bounds)) {
                steamMenu.Cancel();
                TrayMessage("StatusTrayUnavailable");
            }
        }

        private Button CreateTrayButton(NotificationArea.Item item)
        {
            var b = Visuals.Button("StatusBar.TrayItem",item.Name);
            b.Width = 29; b.Focusable = false; b.Foreground = Appearance.Text;
            b.Content = new Image { Width = TrayIconSize, Height = TrayIconSize, Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode((Image)b.Content, BitmapScalingMode.HighQuality);
            b.Click += delegate { InvokeItem(b,false); };
            b.PreviewMouseRightButtonUp += (s,e) => { e.Handled = true; InvokeItem(b,true); };
            b.MouseEnter += delegate { b.Background = Appearance.Selection; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            return b;
        }

        private void Populate(NotificationArea.Item[] items)
        {
            var keys = new HashSet<string>(items.Select(i => i.Key));
            foreach (string key in trayButtons.Keys.Where(k => !keys.Contains(k)).ToArray()) {
                apps.Children.Remove(trayButtons[key]);
                trayButtons.Remove(key);
            }
            if (items.Length == 0) { TrayMessage("StatusTrayEmpty"); return; }
            apps.Children.Remove(trayMessage);
            for (int i = 0; i < items.Length; ++i) {
                var item = items[i];
                Button b;
                if (!trayButtons.TryGetValue(item.Key, out b)) trayButtons.Add(item.Key, b = CreateTrayButton(item));
                b.Tag = item;
                SetHint(b,item.Name);
                ((Image)b.Content).Source = item.Icon;
                int oldIndex = apps.Children.IndexOf(b);
                if (oldIndex == i) continue;
                if (oldIndex >= 0) apps.Children.RemoveAt(oldIndex);
                apps.Children.Insert(i,b);
            }
        }
    }
}
