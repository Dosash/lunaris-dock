using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Lunaris
{
    internal sealed partial class StatusMenus
    {
        private static readonly KeyboardTarget keyboard = new KeyboardTarget();
        private static StatusMenus active;
        private readonly Window owner;
        private Window popup;
        private Button anchor;
        private ScreenInfo screen;
        private Border frame;
        private double menuWidth;
        private StackPanel body;
        private TextBlock heading;
        private Brush palette;
        private LiquidGlassMaterial glass;
        private int networkRequest;
        internal string LanguageLabel { get; private set; }

        static StatusMenus()
        {
            // Both Dock surfaces are non-activating. Clicking them won't raise
            // Deactivated on the menu, so also handle clicks in our own windows.
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent,
                new MouseButtonEventHandler(delegate(object sender, MouseButtonEventArgs e) {
                    var menu = active;
                    if (menu == null || sender == menu.popup) return;
                    if (sender != menu.owner || !menu.anchor.IsMouseOver) menu.Close();
                }), true);
        }

        internal StatusMenus(Window owner)
        {
            this.owner = owner;
            LanguageLabel = KeyboardTarget.Label(keyboard.Layout);
        }

        internal void Update()
        {
            if (active == null) keyboard.Observe(new WindowInteropHelper(owner).Handle);
            LanguageLabel = KeyboardTarget.Label(keyboard.Layout);
            if (popup != null && (palette != Appearance.Surface || glass != Appearance.Glass)) ApplyTheme();
        }

        private bool Begin(Button button, string id, string titleKey)
        {
            bool toggle = anchor == button && popup != null;
            bool switching = active != null;
            if (switching) active.Close();
            if (toggle) return false;
            if (!switching) keyboard.Observe(new WindowInteropHelper(owner).Handle);
            active = this;
            anchor = button;
            var point = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
            var screens = ScreenInfo.Read();
            screen = screens.FirstOrDefault(s => s.Contains(point)) ?? screens[0];
            string title = Loc.Get(titleKey);
            popup = new Window { Owner = owner, Title = title, FontFamily = UiFonts.Text, FontSize = 14,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.Manual, SnapsToDevicePixels = true,
                UseLayoutRounding = true, Width = 300, Height = 100 };
            var window = popup;
            menuWidth = id == "Language" ? 310 : 346;
            Visuals.Identify(window, "StatusMenu." + id, title);
            body = new StackPanel();
            // The first device snapshot and expanded sublists can arrive between
            // layout passes. Size to the settled content before the next paint.
            bool pendingPlacement = false;
            body.SizeChanged += delegate {
                if (pendingPlacement || window != popup) return;
                pendingPlacement = true;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                    pendingPlacement = false;
                    if (popup == window) Place();
                }));
            };
            heading = Text(title);
            heading.FontFamily = UiFonts.Display;
            heading.FontSize = 20;
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(4,0,0,10);
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            var column = new DockPanel();
            DockPanel.SetDock(heading, Dock.Top);
            column.Children.Add(heading);
            column.Children.Add(scroll);
            frame = new Border { Child = column, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1),
                Margin = new Thickness(8,6,8,9), Padding = new Thickness(12,12,12,10), Effect = Visuals.Shadow(8,2,.20) };
            window.Content = frame;
            ApplyTheme();
            window.SourceInitialized += delegate { Visuals.ToolWindow(window, false); };
            window.PreviewKeyDown += (s,e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
            window.Deactivated += delegate { if (popup == window) Close(); };
            window.Closed += delegate {
                if (popup == window) { popup = null; anchor = null; body = null; frame = null; heading = null; }
                if (active == this && popup == null) active = null;
            };
            return true;
        }

        private void ApplyTheme()
        {
            palette = Appearance.Surface;
            glass = Appearance.Glass;
            StatusMenuChrome.Apply(popup, frame);
        }

        private void Place()
        {
            double width = Math.Min(menuWidth + frame.Margin.Left + frame.Margin.Right, screen.WorkArea.Width / screen.Scale);
            Point edge = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            double left = Math.Max(screen.WorkArea.Left, Math.Min(edge.X - width * screen.Scale,
                screen.WorkArea.Right - width * screen.Scale));
            double top = Math.Max(screen.WorkArea.Top, edge.Y);
            double maxHeight = Math.Max(1, Math.Min(500, (screen.WorkArea.Bottom - top) / screen.Scale));
            // A ScrollViewer reports its current viewport while device discovery or
            // an expanded list changes the content. Measure the natural body height
            // independently so the bar only scrolls when the screen actually fills.
            double insetX = frame.Margin.Left + frame.Margin.Right + frame.Padding.Left + frame.Padding.Right +
                frame.BorderThickness.Left + frame.BorderThickness.Right;
            double insetY = frame.Margin.Top + frame.Margin.Bottom + frame.Padding.Top + frame.Padding.Bottom +
                frame.BorderThickness.Top + frame.BorderThickness.Bottom;
            var available = new Size(Math.Max(1, width - insetX), Double.PositiveInfinity);
            body.Measure(available);
            heading.Measure(available);
            double height = Math.Min(maxHeight, Math.Ceiling(body.DesiredSize.Height + heading.DesiredSize.Height + insetY) + 1);
            screen.Place(popup, new Rect(left, top, width * screen.Scale, height * screen.Scale));
        }

        private void Show()
        {
            Place();
            var window = popup;
            if (Appearance.Motion) {
                var pane = frame;
                var shift = new TranslateTransform();
                pane.Opacity = 0;
                pane.RenderTransform = shift;
                window.ContentRendered += delegate {
                    if (popup != window || pane.Opacity != 0) return;
                    pane.Opacity = 1;
                    if (!Appearance.Motion) return;
                    shift.BeginAnimation(TranslateTransform.YProperty, StatusMenuChrome.Motion(-5, 0, 175));
                    pane.BeginAnimation(UIElement.OpacityProperty, StatusMenuChrome.Motion(0, 1, 165));
                };
            }
            window.Show();
            if (popup == window) window.Activate();
        }

        internal void Close()
        {
            var window = popup;
            if (window == null) return;
            popup = null;
            if (active == this) active = null;
            window.Close();
            body = null; frame = null; heading = null; anchor = null;
        }

        private static TextBlock Text(string value)
        {
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,3,0,3) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "MenuText");
            return text;
        }
        private static Button Choice(string id, string label, Action action)
        {
            var button = new Button { Content = StatusMenuChrome.Label(label, 13, "MenuText") };
            button.SetResourceReference(FrameworkElement.StyleProperty, "MenuRow");
            Visuals.Identify(button, "StatusMenu." + id, label);
            button.Click += delegate { action(); };
            return button;
        }
        private void Message(string value)
        {
            body.Children.Add(Text(value));
        }


        internal void OpenLanguage(Button button)
        {
            if (!Begin(button, "Language", "StatusLanguage")) return;
            IntPtr selected = keyboard.Layout;
            foreach (Forms.InputLanguage layout in KeyboardTarget.Installed) {
                IntPtr handle = layout.Handle;
                bool current = selected == handle;
                string name = layout.Culture.NativeName + " · " + layout.LayoutName;
                var option = Choice("Layout", name, delegate {
                    Close();
                    if (!keyboard.Select(handle)) button.ToolTip = Visuals.Tip(Loc.Get("StatusLayoutDenied"));
                });
                var line = new DockPanel();
                var code = StatusMenuChrome.Label(KeyboardTarget.Label(handle), 12, "MenuText");
                code.HorizontalAlignment = HorizontalAlignment.Center;
                code.FontWeight = FontWeights.SemiBold;
                var keycap = new Border { Width = 34, Height = 26, CornerRadius = new CornerRadius(6),
                    Child = code, BorderThickness = new Thickness(1,1,1,2), Margin = new Thickness(0,0,10,0),
                    VerticalAlignment = VerticalAlignment.Center };
                keycap.SetResourceReference(Border.BackgroundProperty, "MenuPanel");
                keycap.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
                line.Children.Add(keycap);
                if (current) {
                    option.SetResourceReference(Control.BackgroundProperty, "MenuSelection");
                    var mark = StatusMenuChrome.Glyph("\uE73E", 14, "MenuAccent");
                    DockPanel.SetDock(mark, Dock.Right);
                    mark.Margin = new Thickness(8,0,0,0);
                    line.Children.Add(mark);
                }
                var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var language = StatusMenuChrome.Label(layout.Culture.NativeName,14,"MenuText");
                language.FontWeight = FontWeights.SemiBold;
                labels.Children.Add(language);
                if (!String.Equals(layout.LayoutName, layout.Culture.NativeName, StringComparison.CurrentCultureIgnoreCase))
                    labels.Children.Add(StatusMenuChrome.Label(layout.LayoutName,12,"MenuMuted"));
                line.Children.Add(labels);
                option.Content = line;
                option.MinHeight = 45;
                AutomationProperties.SetItemStatus(option, unchecked((uint)handle.ToInt64()).ToString("X8"));
                if (current) AutomationProperties.SetHelpText(option, "current");
                body.Children.Add(option);
            }
            Show();
        }

        internal void OpenNetwork(Button button)
        {
            if (!Begin(button, "Network", "StatusNetwork")) return;
            Message(Loc.Get("StatusLoading"));
            Show();
            if (popup != null) LoadNetworks(false, null);
        }

        private void LoadNetworks(bool refresh, string message)
        {
            var window = popup;
            body.Children.Clear();
            var details = new StackPanel();
            var radios = new QuickRadioPanel(delegate { if (popup == window) Place(); },
                delegate { if (popup == window && body.Children.Contains(details)) ReadNetworks(details, true, null); });
            body.Children.Add(radios);
            body.Children.Add(StatusMenuChrome.Rule());
            body.Children.Add(details);
            var networkButton = anchor;
            var more = Choice("QuickSettings", Loc.Get("QuickMore"), delegate {
                Close();
                if (!WinKey.ShellShortcut('A')) networkButton.ToolTip = Visuals.Tip(Loc.Get("StatusShortcutBusy"));
            });
            var footer = new DockPanel();
            var arrow = StatusMenuChrome.Glyph("\uE8A7", 13, "MenuMuted");
            DockPanel.SetDock(arrow, Dock.Right);
            footer.Children.Add(arrow);
            footer.Children.Add(StatusMenuChrome.Label(Loc.Get("QuickMore"),13,"MenuText"));
            more.Content = footer;
            body.Children.Add(StatusMenuChrome.Rule());
            body.Children.Add(more);
            ReadNetworks(details, refresh, message);
        }

        private async void ReadNetworks(StackPanel details, bool refresh, string message)
        {
            int request = ++networkRequest;
            details.Children.Clear();
            details.Children.Add(Text(Loc.Get("StatusLoading")));
            Place();
            WirelessNetworks snapshot = await Task.Run(() => WirelessNetworks.Read(refresh));
            if (request != networkRequest || body == null || !body.Children.Contains(details)) return;
            details.Children.Clear();
            details.Children.Add(NetworkState("Ethernet", "Ethernet", snapshot.Ethernet));
            details.Children.Add(NetworkState("Wifi", "Wi-Fi", snapshot.Wifi));
            var rows = new StackPanel();
            string title = Loc.Get("StatusAvailableNetworks");
            var list = new GroupBox { Header = title, Content = rows, Margin = new Thickness(0,9,0,0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            list.SetResourceReference(Control.ForegroundProperty, "MenuText");
            list.SetResourceReference(Control.BorderBrushProperty, "MenuBorder");
            Visuals.Identify(list, "StatusMenu.NetworkList", title);
            details.Children.Add(list);
            foreach (var net in snapshot.Networks.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal)) {
                string label = (net.Connected ? "✓  " : "") + net.Name + "  ·  " + net.Signal + "%";
                if (snapshot.Adapters > 1) label += "  ·  " + net.AdapterName;
                if (net.HasProfile && net.Profile != net.Name) label += "  ·  " + net.Profile;
                rows.Children.Add(Choice("NetworkRow", label, () => ChooseNetwork(net)));
            }
            string state = null;
            if (snapshot.Error != 0)
                state = snapshot.Error == 5 ? Loc.Get("StatusWifiAccess") : Loc.Format("StatusWifiError", new Win32Exception(snapshot.Error).Message);
            else if (snapshot.Networks.Count == 0)
                state = Loc.Get(snapshot.Adapters == 0 ? "StatusNoWifi" : "StatusNoNetworks");
            if (state != null) {
                var explanation = StatusMenuChrome.Label(state, 13, "MenuMuted");
                explanation.Margin = new Thickness(4,2,4,3);
                Visuals.Identify(explanation, "StatusMenu.NetworkListState", state);
                rows.Children.Add(explanation);
            }
            if (message != null) details.Children.Add(Text(message));
            if (snapshot.Adapters > 0) details.Children.Add(Choice("Refresh", Loc.Get("StatusRescan"), () => ReadNetworks(details, true, null)));
            Place();
        }

        private static FrameworkElement NetworkState(string id, string name, bool connected)
        {
            string state = Loc.Get(connected ? "StatusLinkUp" : "StatusLinkDown");
            var line = new DockPanel { Margin = new Thickness(4,3,4,3) };
            var status = StatusMenuChrome.Label(state, 12, "MenuMuted");
            DockPanel.SetDock(status,Dock.Right);
            line.Children.Add(status);
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            dot.SetResourceReference(Border.BackgroundProperty, connected ? "MenuOnline" : "MenuSwitchOff");
            line.Children.Add(dot);
            var label = StatusMenuChrome.Label(name, 13, "MenuText");
            Visuals.Identify(label, "StatusMenu." + id, name + "  ·  " + state);
            line.Children.Add(label);
            return line;
        }

        private void ChooseNetwork(WifiNetwork net)
        {
            body.Children.Clear();
            Message(net.Name);
            PasswordBox password = null;
            if (!net.Connected && !net.Supported) Message(Loc.Get("StatusWifiProfile"));
            else {
                if (!net.Connected && net.NeedsKey) {
                    string label = Loc.Get("StatusWifiPassword");
                    Message(label);
                    password = new PasswordBox { MaxLength = 64, Padding = new Thickness(6,4,6,4) };
                    password.SetResourceReference(Control.BackgroundProperty, "MenuPanel");
                    password.SetResourceReference(Control.ForegroundProperty, "MenuText");
                    password.SetResourceReference(Control.BorderBrushProperty, "MenuBorder");
                    Visuals.Identify(password, "StatusMenu.Password", label);
                    body.Children.Add(password);
                }
                Button submit = Choice(net.Connected ? "Disconnect" : "Connect",
                    Loc.Get(net.Connected ? "StatusDisconnect" : "StatusConnect"), delegate {
                        string key = password == null ? null : password.Password;
                        if (password != null) password.Clear();
                        ChangeNetwork(net, key);
                    });
                submit.IsDefault = true;
                body.Children.Add(submit);
            }
            body.Children.Add(Choice("Back", Loc.Get("Cancel"), () => LoadNetworks(false, null)));
            Place();
            if (password != null) password.Focus();
        }

        private async void ChangeNetwork(WifiNetwork network, string key)
        {
            var window = popup;
            body.Children.Clear();
            Message(Loc.Get(network.Connected ? "StatusDisconnecting" : "StatusConnecting"));
            Place();
            string error = null;
            try { await Task.Run(() => WirelessNetworks.Change(network, network.Connected, key)); }
            catch (Win32Exception ex) { error = Loc.Format("StatusWifiError", ex.Message); }
            if (popup == window) LoadNetworks(false, error);
        }
    }
}
