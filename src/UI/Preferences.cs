using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lunaris
{
    internal sealed partial class Preferences : Window
    {
        private readonly DesktopUi ui;
        private readonly FrameworkElement view;
        private readonly ComboBox monitors;
        private string monitorChoice;
        private bool refreshing;
        private readonly TranslateTransform lift = new TranslateTransform();
        private bool dismissing, immediateClose;
        private int motionVersion;

        internal Preferences(DesktopUi ui)
        {
            this.ui = ui;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Topmost = true;
            UseLayoutRounding = true; FontFamily = UiFonts.Text; FontSize = 14;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 506; Height = 555;
            using (var stream = typeof(Preferences).Assembly.GetManifestResourceStream("Lunaris.Preferences.xaml"))
                view = (FrameworkElement)XamlReader.Load(stream);
            Content = view;
            view.RenderTransform = lift;
            monitors = Part<ComboBox>("DockMonitor");
            monitorChoice = ui.Settings.Monitor;
            var languages = Part<ComboBox>("UiLanguage");
            for (int i = 0; i < Loc.Codes.Length; ++i)
            {
                string code = Loc.Codes[i], name = Loc.Names[i];
                var item = new ComboBoxItem { Tag = code, Content = name };
                Visuals.Identify(item, "Language." + code, name);
                languages.Items.Add(item);
                if (code == ui.Settings.Language) languages.SelectedItem = item;
            }
            languages.SelectionChanged += delegate {
                var item = languages.SelectedItem as ComboBoxItem;
                if (item != null) ui.ChangeLanguage((string)item.Tag);
            };
            ApplyTheme();
            var options = Part<Panel>("ThemeOptions");
            foreach (var mode in new[] { "system", "light", "dark", "custom" })
            {
                var choice = new RadioButton {
                    GroupName = "Theme", Tag = mode, Style = (Style)view.FindResource("ThemeChoice"),
                    IsChecked = mode == ui.Settings.Theme
                };
                if (mode == "custom") customChoice = choice;
                choice.Checked += delegate {
                    if (ui.Settings.Theme == mode) return;
                    ui.Settings.Theme = mode;
                    ui.Changed();
                };
                options.Children.Add(choice);
            }
            var size = Part<Slider>("DockSize");
            var reading = Part<TextBlock>("SizeValue");
            size.Value = ui.Settings.Size;
            size.ValueChanged += delegate {
                reading.Text = Loc.Format("SizePoints", Math.Round(size.Value));
                ui.ResizeDock(size.Value);
            };
            BindSwitch("Magnify", () => ui.Settings.Magnify, v => ui.Settings.Magnify = v);
            BindSwitch("LiquidGlass", () => ui.Settings.LiquidGlass, v => ui.Settings.LiquidGlass = v);
            BindSwitch("ShowUpdateBadge", () => ui.Settings.ShowUpdateBadge, v => ui.Settings.ShowUpdateBadge = v);
            BindSwitch("HideDockWhenCovered", () => ui.Settings.HideDockWhenCovered, v => ui.Settings.HideDockWhenCovered = v);
            BindSwitch("Startup", () => ui.Settings.Startup, v => ui.Settings.Startup = v);
            BindSwitch("ReplaceTaskbar", () => ui.Settings.ReplaceTaskbar, v => ui.Settings.ReplaceTaskbar = v);
            BindSwitch("HideWindowsStart", () => ui.Settings.HideWindowsStart, v => ui.Settings.HideWindowsStart = v);
            monitors.SelectionChanged += delegate {
                var item = monitors.SelectedItem as ComboBoxItem;
                if (refreshing || item == null || monitorChoice == (string)item.Tag) return;
                monitorChoice = ui.Settings.Monitor = (string)item.Tag;
                ui.Changed();
            };
            ApplyLanguage();
            Part<Button>("ProjectLink").Click += delegate { OpenLink(ReleaseUpdates.ProjectUrl); };
            Part<Hyperlink>("SupportLink").RequestNavigate += (sender, e) => {
                e.Handled = true;
                OpenLink(e.Uri.AbsoluteUri);
            };
            Part<Button>("ReleaseLink").Click += delegate {
                var release = ui.AvailableUpdate;
                if (release != null) OpenLink(release.Url);
            };
            Part<Button>("CloseSettings").Click += delegate { Close(); };
            Part<Grid>("Toolbar").MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) {
                if (e.LeftButton == MouseButtonState.Pressed) DragMove();
            };
            PreviewKeyDown += delegate(object s, KeyEventArgs e) {
                if (e.Key == Key.Escape && !monitors.IsDropDownOpen && !languages.IsDropDownOpen) { e.Handled = true; Close(); }
            };
            SourceInitialized += delegate { Visuals.ToolWindow(this, false); PositionOnMonitor(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (immediateClose || !IsVisible || !Appearance.Motion) return;
                e.Cancel = true;
                if (dismissing) return;
                dismissing = true;
                view.IsHitTestVisible = false;
                AnimateWindow(false);
            };
        }

        internal void Present()
        {
            if (IsVisible && !dismissing) return;
            bool first = !IsVisible;
            dismissing = false;
            view.IsHitTestVisible = true;
            if (first)
            {
                view.Opacity = Appearance.Motion ? 0 : 1;
                lift.Y = Appearance.Motion ? 9 : 0;
                Show();
            }
            AnimateWindow(true);
        }

        internal void CloseImmediately()
        {
            ++motionVersion;
            immediateClose = true;
            Close();
        }

        private void AnimateWindow(bool opening)
        {
            int generation = ++motionVersion;
            double previousOpacity = view.Opacity, previousY = lift.Y;
            view.BeginAnimation(OpacityProperty, null);
            lift.BeginAnimation(TranslateTransform.YProperty, null);
            view.Opacity = opening ? 1 : 0;
            lift.Y = opening ? 0 : 7;
            if (!Appearance.Motion)
            {
                if (!opening) CloseImmediately();
                return;
            }
            var duration = TimeSpan.FromMilliseconds(opening ? 225 : 165);
            var ease = new SineEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseInOut };
            var fade = new DoubleAnimation(previousOpacity, view.Opacity, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            if (!opening) fade.Completed += delegate { if (generation == motionVersion && dismissing) CloseImmediately(); };
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(previousY, lift.Y, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            view.BeginAnimation(OpacityProperty, fade);
        }

        private T Part<T>(string name) where T : DependencyObject { return (T)view.FindName(name); }

        private void BindSwitch(string name, Func<bool> read, Action<bool> update)
        {
            var box = Part<CheckBox>(name);
            box.IsChecked = read();
            box.Loaded += delegate { PaintSwitch(box, false); };
            RoutedEventHandler changed = delegate {
                bool value = box.IsChecked == true;
                PaintSwitch(box, box.IsLoaded && Appearance.Motion);
                if (value == read()) return;
                update(value);
                ui.Changed();
                if (read() != value) box.IsChecked = read();
            };
            box.Checked += changed;
            box.Unchecked += changed;
        }

        private static void PaintSwitch(CheckBox box, bool animate)
        {
            var knob = box.Template == null ? null : box.Template.FindName("thumb", box) as Border;
            var tint = box.Template == null ? null : box.Template.FindName("onTrack", box) as Border;
            if (knob == null || tint == null) return;
            var offset = knob.RenderTransform as TranslateTransform;
            if (offset == null || offset.IsFrozen) knob.RenderTransform = offset = new TranslateTransform();
            bool on = box.IsChecked == true;
            double x = on ? 14 : 0, alpha = on ? 1 : 0;
            double fromX = offset.X, fromAlpha = tint.Opacity;
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            tint.BeginAnimation(OpacityProperty, null);
            offset.X = x;
            tint.Opacity = alpha;
            if (animate)
            {
                var duration = TimeSpan.FromMilliseconds(165);
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
                offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, x, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                tint.BeginAnimation(OpacityProperty, new DoubleAnimation(fromAlpha, alpha, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            }
        }

        internal void ApplyTheme()
        {
            Resources["Ink"] = Appearance.Text; Resources["Muted"] = Appearance.Muted;
            Resources["Surface"] = Appearance.Surface; Resources["Panel"] = Appearance.Panel;
            var material = Appearance.Glass;
            Resources["WindowSurface"] = material == null ? (Brush)Appearance.Surface : material.Pane;
            Resources["WindowRim"] = material == null ? (Brush)Appearance.Border : material.Rim;
            Resources["GroupSurface"] = material == null ? (Brush)Appearance.Panel : material.Group;
            Resources["Rule"] = Appearance.Border; Resources["Accent"] = Appearance.Accent;
            Resources["Tint"] = Appearance.Selection;
            Resources["Track"] = Appearance.Custom ? Appearance.Muted : Visuals.Brush(Appearance.Dark ? "#657081" : "#929DAA");
            Resources["On"] = Appearance.Custom ? Appearance.Accent : Visuals.Brush(Appearance.Dark ? "#0F81DE" : "#1973C4");
            Foreground = Appearance.Text;
            if (customChoice != null) UpdateColorEditor();
        }

        internal void ApplyLanguage()
        {
            Language = XmlLanguage.GetLanguage(Loc.Language);
            Title = Loc.Get("SettingsTitle");
            Visuals.Identify(this, "Settings", Title);
            foreach (var entry in Loc.Strings) Resources["Strings." + entry.Key] = entry.Value;
            foreach (RadioButton choice in Part<Panel>("ThemeOptions").Children)
            {
                var mode = (string)choice.Tag;
                string label = Loc.Get("Theme." + mode);
                choice.Content = ThemeExample(mode, label);
                Visuals.Identify(choice, "Theme." + Char.ToUpperInvariant(mode[0]) + mode.Substring(1), label);
            }
            Part<Panel>("ColorFields").Children.Clear();
            BuildColorEditor();
            UpdateColorEditor();
            Part<TextBlock>("SizeValue").Text = Loc.Format("SizePoints", Math.Round(ui.Settings.Size));
            RefreshUpdates();
            RefreshMonitors();
        }

        internal void RefreshUpdates()
        {
            var release = ui.AvailableUpdate;
            Part<TextBlock>("CurrentVersion").Text = Loc.Format("CurrentVersion", ReleaseUpdates.CurrentVersion);
            Part<Button>("ProjectLink").ToolTip = ReleaseUpdates.ProjectUrl;
            var link = Part<Button>("ReleaseLink");
            link.Content = release == null ? null : Loc.Format("ReleaseLink", release.Version);
            link.ToolTip = release == null ? null : release.Url;
            link.Visibility = release == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OpenLink(string url)
        {
            try
            {
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                if (!(ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)) throw;
                MessageBox.Show(this, Loc.Get("LinkError") + "\n\n" + ex.Message, "Lunaris Dock", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        internal void RefreshMonitors()
        {
            refreshing = true;
            try
            {
                var screens = ui.Monitors;
                monitors.Items.Clear();
                Part<Panel>("MonitorRow").Visibility = screens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
                AddMonitor(Loc.Get("MainDisplay"), "primary", "Monitor.primary");
                for (int i = 0; i < screens.Length; i++)
                    AddMonitor(screens[i].Label, screens[i].Id, "Monitor." + i);
                AddMonitor(Loc.Get("AllDisplays"), "all", "Monitor.all");
                monitors.SelectedItem = monitors.Items.Cast<ComboBoxItem>().FirstOrDefault(
                    m => String.Equals((string)m.Tag, monitorChoice, StringComparison.OrdinalIgnoreCase)) ?? monitors.Items[0];
            }
            finally { refreshing = false; }
        }

        private void AddMonitor(string caption, string value, string id)
        {
            var item = new ComboBoxItem { Content = caption, Tag = value };
            Visuals.Identify(item, id, caption);
            monitors.Items.Add(item);
        }

        private void PositionOnMonitor()
        {
            var screen = ui.ActiveMonitor;
            if (screen != null) SizeToScreen(screen,null);
        }

        private void SizeToScreen(ScreenInfo screen, Point? anchor)
        {
            var area = screen.WorkArea;
            double width = Math.Min(506, area.Width / screen.Scale);
            double preferred = (ui.Monitors.Length > 1 ? 736 : 693) + (ui.Settings.Theme == "custom" ? 74 : 0)
                + 225 + (ui.AvailableUpdate == null ? 0 : 32);
            double height = Math.Min(preferred, area.Height / screen.Scale);
            Width = width; Height = height;
            double w = Math.Round(width * screen.Scale), h = Math.Round(height * screen.Scale);
            double x = anchor.HasValue ? Math.Max(area.Left, Math.Min(anchor.Value.X, area.Right - w)) : area.Left + (area.Width - w) / 2;
            double y = anchor.HasValue ? Math.Max(area.Top, Math.Min(anchor.Value.Y, area.Bottom - h)) : area.Top + (area.Height - h) / 2;
            screen.Place(this, new Rect(Math.Round(x),Math.Round(y),w,h));
        }

        // Preview the same translucent panel and bottom Dock in each appearance.
        private FrameworkElement ThemeExample(string mode, string caption)
        {
            var picture = new DrawingGroup();
            using (var dc = picture.Open())
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, 124, 54), 6, 6));
                if (mode == "custom") PaintCustom(dc); else PaintExample(dc, mode == "dark");
                if (mode == "system") {
                    dc.PushClip(new RectangleGeometry(new Rect(62, 0, 62, 54)));
                    PaintExample(dc, true); dc.Pop();
                }
                dc.Pop();
            }
            picture.Freeze();
            var stack = new StackPanel();
            stack.Children.Add(new Image { Source = new DrawingImage(picture), Height = 49, Stretch = Stretch.Uniform });
            var text = new TextBlock { Text = caption, FontSize = 13, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            stack.Children.Add(text);
            return stack;
        }

        private static void PaintExample(DrawingContext dc, bool night)
        {
            var sky = new LinearGradientBrush(Visuals.Brush(night ? "#172C57" : "#A8D7F0").Color,
                Visuals.Brush(night ? "#415B94" : "#739ECA").Color, 70);
            dc.DrawRectangle(sky, null, new Rect(0,0,124,54));
            dc.DrawGeometry(Visuals.Brush(night ? "#324A73" : "#80B8C9"), null,
                Geometry.Parse("M0,53 Q31,10 65,31 Q90,42 124,7 L124,54 L0,54 Z"));
            dc.DrawGeometry(Visuals.Brush(night ? "#253555" : "#587FAF"), null,
                Geometry.Parse("M0,48 Q33,31 59,43 Q90,49 124,25 L124,54 L0,54 Z"));
            var pane = Visuals.Brush(night ? "#E53A4150" : "#EAF7FBFF");
            dc.DrawRoundedRectangle(pane, null, new Rect(34,8,57,32),4,4);
            var ink = Visuals.Brush(night ? "#B5C7DD" : "#5C758D");
            dc.DrawRoundedRectangle(ink,null,new Rect(40,13,23,2),1,1);
            for (int i = 0; i < 4; i++) {
                var color = new[] {"#6EACD1","#7F99D0","#A7A2CF","#83B8AE"}[i];
                dc.DrawRoundedRectangle(Visuals.Brush(color),null,new Rect(40+i*12,21,8,9),2,2);
            }
            dc.DrawRoundedRectangle(pane,null,new Rect(43,46,38,5),2.5,2.5);
        }
    }
}
