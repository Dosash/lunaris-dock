using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Lunaris
{
    internal sealed partial class LaunchSurface : Window
    {
        private readonly DesktopUi ui;
        private readonly Grid surface = new Grid();
        private readonly Grid layout = new Grid();
        private readonly Border glass = new Border();
        private readonly Border shadow = new Border();
        private readonly Border headerRule = new Border();
        private readonly Border searchFrame = new Border();
        private readonly TextBox search = new TextBox();
        private readonly Button findFiles;
        private readonly Button addToEmptyFolder;
        private readonly TextBlock title, placeholder, searchGlyph, empty;
        private readonly Button more;
        private readonly Border moreCircle = new Border();
        private readonly WrapPanel pills = new WrapPanel();
        private readonly Grid grid = new Grid();
        private readonly LaunchNavigationMotion navigation;
        private readonly StackPanel pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly DispatcherTimer outside;
        private readonly ScaleTransform zoom = new ScaleTransform();
        private ScreenInfo panelScreen;
        private Rect launchButton = Rect.Empty;
        private IntPtr launchDock;
        private bool inFlight;
        private readonly List<LibraryItem> results = new List<LibraryItem>();
        private readonly List<Border> frames = new List<Border>();
        private int columns = 5, rows = 4, selected;
        private AnimationFrameClock flightClock;
        private LaunchComposition composition;
        private DispatcherTimer flightEnd;
        private Func<double> composedProgress;
        private double startScale, endScale, startAlpha, endAlpha;
        private double CurrentScale { get { return composedProgress == null ? zoom.ScaleX : startScale + (endScale-startScale)*composedProgress(); } }
        private double CurrentAlpha { get { return composedProgress == null ? surface.Opacity : startAlpha + (endAlpha-startAlpha)*composedProgress(); } }
        private int powerDialogRequested;
        private double cellWidth = 120, iconSize = 58;
        private string category = "All";
        private bool opening, closing, mouseDown, clearingQuery;
        private BitmapSource backdrop;
        private Rect lastBounds = Rect.Empty;
        private int wheelRemainder, lastWheelTime;
        private int Capacity { get { return rows * columns; } }
        private int Page { get { return selected / Capacity; } }
        private int Pages { get { return Math.Max(1, (results.Count + Capacity - 1) / Capacity); } }
        internal bool IsOpen { get { return IsVisible && !closing; } }

        internal LaunchSurface(DesktopUi ui)
        {
            this.ui = ui;
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Language);
            Title = Loc.Get("StartTitle");
            Visuals.Identify(this, "Launchpad", Title);
            FontFamily = UiFonts.Text; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Topmost = true; UseLayoutRounding = true;
            AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 660; Height = 568; Left = Top = 0;
            SourceInitialized += delegate {
                Visuals.ToolWindow(this, false);
                var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                if (source != null) source.AddHook(OnSystemAppearanceChanged);
            };

            glass.CornerRadius = shadow.CornerRadius = new CornerRadius(25);
            glass.Margin = shadow.Margin = new Thickness(13,8,13,13);
            shadow.IsHitTestVisible = false; shadow.BorderThickness = new Thickness(1);
            glass.BorderThickness = new Thickness(1);
            glass.Padding = new Thickness(19, 12, 19, 6);
            var layers = new Grid();
            layers.Children.Add(layout);
            layers.Children.Add(editor);
            glass.Child = layers;
            surface.RenderTransform = zoom;
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
            var header = new Grid { Height = 48, Margin = new Thickness(1,0,1,7) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var logo = Visuals.LaunchIcon(22);
            logo.VerticalAlignment = VerticalAlignment.Center;
            logo.Margin = new Thickness(0,0,10,0);
            header.Children.Add(logo);
            navigationLogo = logo;
            backButton = Visuals.Button("Launchpad.Folder.Back", Loc.Get("UpLevel"));
            backButton.Width = 29;
            backButton.Margin = new Thickness(0, 0, 4, 0);
            backButton.Content = Glyph("\uE72B", 17);
            backButton.Click += delegate { NavigateUp(); };
            backButton.AllowDrop = true;
            backButton.DragOver += delegate(object sender, DragEventArgs e) { DragOverFolder(e, ParentFolder()); };
            backButton.Drop += delegate(object sender, DragEventArgs e) { DropInto(e, ParentFolder()); };
            backButton.Visibility = Visibility.Collapsed;
            header.Children.Add(backButton);
            title = Text(Loc.Get("Apps"), 24); title.FontFamily = UiFonts.Display; title.FontWeight = FontWeights.Medium;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Margin = new Thickness(0, 0, 8, 0);
            Visuals.Identify(title, "Launchpad.Title", title.Text);
            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            breadcrumb = Text("", 11);
            breadcrumb.TextTrimming = TextTrimming.CharacterEllipsis;
            breadcrumb.Visibility = Visibility.Collapsed;
            Visuals.Identify(breadcrumb, "Launchpad.Folder.Path", Loc.Get("FolderPath"));
            heading.Children.Add(breadcrumb);
            heading.Children.Add(title);
            Grid.SetColumn(heading,1); header.Children.Add(heading);

            searchFrame.Width = 160; searchFrame.Height = 29; searchFrame.VerticalAlignment = VerticalAlignment.Center;
            searchFrame.CornerRadius = new CornerRadius(14); searchFrame.BorderThickness = new Thickness(1);
            var field = new Grid { Margin = new Thickness(9,0,8,0) };
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            field.ColumnDefinitions.Add(new ColumnDefinition());
            searchGlyph = Glyph("\uE721",12); field.Children.Add(searchGlyph);
            placeholder = Text(Loc.Get("Search"),13); Grid.SetColumn(placeholder,1); field.Children.Add(placeholder);
            search.FontFamily = UiFonts.Text; search.FontSize = 13; search.Background = Brushes.Transparent;
            search.VerticalContentAlignment = VerticalAlignment.Center; search.BorderThickness = new Thickness(0);
            search.Padding = new Thickness(0); Visuals.Identify(search,"Search",Loc.Get("SearchApps"));
            search.MaxLength = 1024;
            Grid.SetColumn(search,1); field.Children.Add(search); searchFrame.Child = field;
            Grid.SetColumn(searchFrame,2); header.Children.Add(searchFrame);
            more = Visuals.Button("Launchpad.More",Loc.Get("Actions")); more.Width = 35; more.Height = 32;
            more.Margin = new Thickness(7,0,0,0);
            moreCircle.Width = moreCircle.Height = 22; moreCircle.CornerRadius = new CornerRadius(11);
            moreCircle.BorderThickness = new Thickness(1.5);
            var dots = Text("···",18); dots.FontWeight = FontWeights.Bold; dots.HorizontalAlignment = HorizontalAlignment.Center;
            dots.Margin = new Thickness(0,-3,0,0); moreCircle.Child = dots;
            more.Content = moreCircle; more.Click += delegate { OpenActions(); };
            Grid.SetColumn(more,3); header.Children.Add(more);
            headerRule.BorderThickness = new Thickness(0,0,0,1); headerRule.Child = header;
            layout.Children.Add(headerRule);
            pills.Margin = new Thickness(0,13,0,12);
            Grid.SetRow(pills,1); layout.Children.Add(pills);
            navigation = new LaunchNavigationMotion(delegate { return IsOpen && !EditingFolder && Appearance.Motion; });
            var fieldArea = navigation.Viewport;
            fieldArea.AllowDrop = true;
            fieldArea.DragOver += delegate(object sender, DragEventArgs e) { DragOverFolder(e, folderId); };
            fieldArea.Drop += delegate(object sender, DragEventArgs e) { DropInto(e, folderId); };
            navigation.Content.Children.Add(grid);
            empty = Text("",15); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.TextAlignment = TextAlignment.Center;
            var searchStatus = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            empty.TextWrapping = TextWrapping.Wrap; searchStatus.Children.Add(empty);
            findFiles = Visuals.Button("Launchpad.FindFiles",Loc.Get("FindFiles"));
            findFiles.Content = new TextBlock { Text = Loc.Get("FindFiles"), Margin = new Thickness(12,6,12,6) };
            findFiles.Margin = new Thickness(0,8,0,0);
            findFiles.Click += delegate { SearchFiles(); };
            searchStatus.Children.Add(findFiles);
            addToEmptyFolder = Visuals.Button("Launchpad.Folder.AddEmpty",Loc.Get("AddApps"));
            addToEmptyFolder.Content = new TextBlock { Text = Loc.Get("AddApps"), Margin = new Thickness(12,6,12,6) };
            addToEmptyFolder.Margin = new Thickness(0,8,0,0);
            addToEmptyFolder.Click += delegate { AddApps(folderId); };
            searchStatus.Children.Add(addToEmptyFolder);
            navigation.Content.Children.Add(searchStatus);
            Grid.SetRow(fieldArea,2); layout.Children.Add(fieldArea);
            Grid.SetRow(pager,3); layout.Children.Add(pager);
            surface.Children.Add(shadow); surface.Children.Add(glass); Content = surface;
            PreviewMouseDown += delegate { if (!glass.IsMouseOver && IsOpen) Dismiss(); };
            search.TextChanged += delegate { if (!opening && !clearingQuery) { selected = 0; RefreshCatalog(); } };
            PreviewKeyDown += OnKey;
            PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) {
                if (EditingFolder || e.Delta == 0) return;
                if (Math.Sign(wheelRemainder) != Math.Sign(e.Delta) || unchecked(e.Timestamp-lastWheelTime) > 350)
                    wheelRemainder = 0;
                lastWheelTime = e.Timestamp;
                wheelRemainder += e.Delta;
                int steps = wheelRemainder / 120;
                wheelRemainder %= 120;
                if (steps != 0) ChangePage(-steps);
                e.Handled = true;
            };
            outside = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(45) };
            outside.Tick += delegate {
                bool down = LaunchGlass.ButtonsDown();
                if (down && !mouseDown && IsOpen && !ui.IconEditorOpen && LaunchGlass.Outside(new WindowInteropHelper(this).Handle)) Dismiss();
                mouseDown = down;
            };
            Deactivated += delegate {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                    if (IsOpen && !opening && !ui.IconEditorOpen && !IsActive && LaunchGlass.LostForeground()) Dismiss();
                }));
            };
            Closed += delegate {
                outside.Stop(); navigation.Cancel(); StopFlightFrames();
                if (composition != null) composition.Dispose();
            };
            ApplyTheme();
        }

        private static TextBlock Text(string text, double size)
        {
            return new TextBlock { Text = text, FontFamily = UiFonts.Text, FontSize = size, Foreground = Appearance.Text,
                VerticalAlignment = VerticalAlignment.Center };
        }

        private static TextBlock Glyph(string glyph, double size)
        {
            var text = Text(glyph,size); text.FontFamily = new FontFamily("Segoe MDL2 Assets");
            text.HorizontalAlignment = HorizontalAlignment.Center; return text;
        }

        private static SolidColorBrush Translucent(SolidColorBrush tone, byte opacity)
        {
            var c = tone.Color; c.A = opacity;
            var b = new SolidColorBrush(c); b.Freeze(); return b;
        }

        internal void ApplyTheme()
        {
            navigation.Cancel();
            navigationLogo.Source = LaunchArtwork.ForTheme();
            PaintGlass();
            headerRule.BorderBrush = Translucent(Appearance.Muted,57);
            title.Foreground = Appearance.Text; searchGlyph.Foreground = Appearance.Muted; placeholder.Foreground = Appearance.Muted;
            breadcrumb.Foreground = Appearance.Muted;
            search.Foreground = Appearance.Text; search.CaretBrush = Appearance.Text; search.SelectionBrush = Appearance.Selection;
            var material = Appearance.Glass;
            searchFrame.Background = material == null ? Translucent(Appearance.Panel,143) : material.Group;
            searchFrame.BorderBrush = material == null ? Translucent(Appearance.Muted,60) : material.Rim;
            moreCircle.BorderBrush = Appearance.Muted; ((TextBlock)moreCircle.Child).Foreground = Appearance.Text;
            empty.Foreground = Appearance.Muted;
            findFiles.Foreground = Appearance.Text;
            findFiles.Background = Appearance.Selection;
            addToEmptyFolder.Foreground = Appearance.Text;
            addToEmptyFolder.Background = Appearance.Selection;
            BuildPills(); RenderPage(); UpdateEditorTheme();
        }

        private void PaintGlass()
        {
            var material = Appearance.Glass;
            if (material != null && !LaunchGlass.Enabled()) material = null;
            shadow.Background = material == null ? Appearance.Surface : null;
            shadow.BorderBrush = material == null ? null : Appearance.Border;
            shadow.Effect = Visuals.Shadow(15,5,Appearance.Dark ? .52 : .34);
            if (material != null)
            {
                glass.BorderBrush = material.Rim;
                if (backdrop == null) { glass.Background = material.Pane; return; }
                var optics = new DrawingGroup();
                using (var dc = optics.Open())
                {
                    var bounds = new Rect(0, 0, 1, 1);
                    // Blend the refracted sample with the live desktop underneath.
                    dc.PushOpacity(.55);
                    dc.DrawImage(LaunchGlass.Refract(backdrop), bounds);
                    dc.Pop();
                    dc.PushOpacity(.66);
                    dc.DrawRectangle(material.Pane, null, bounds);
                    dc.Pop();
                }
                var lens = new DrawingBrush(optics) { Stretch = Stretch.Fill };
                lens.Freeze();
                glass.Background = lens;
                RenderOptions.SetBitmapScalingMode(glass,BitmapScalingMode.HighQuality);
                return;
            }
            var rim = new LinearGradientBrush();
            rim.StartPoint = new Point(0,0); rim.EndPoint = new Point(.35,1);
            Color panel = Appearance.Panel.Color, surfaceColor = Appearance.Surface.Color, accent = Appearance.Accent.Color;
            rim.GradientStops.Add(new GradientStop(GlassTint(panel, Colors.White, Appearance.Dark ? .47 : .90,
                (byte)(Appearance.Dark ? 178 : 240)),0));
            rim.GradientStops.Add(new GradientStop(GlassTint(Appearance.Border.Color, accent, .13,
                (byte)(Appearance.Dark ? 155 : 196)),1));
            rim.Freeze(); glass.BorderBrush = rim;
            if (backdrop == null || !LaunchGlass.Enabled()) { glass.Background = Appearance.Surface; return; }
            var image = new DrawingGroup();
            using (var context = image.Open()) {
                var rect = new Rect(0,0,1,1);
                context.DrawImage(backdrop,rect);
                var veil = new LinearGradientBrush();
                veil.StartPoint = new Point(.1,0); veil.EndPoint = new Point(.9,1);
                veil.GradientStops.Add(new GradientStop(GlassTint(surfaceColor,panel,Appearance.Dark ? .14 : .85,
                    (byte)(Appearance.Custom ? 223 : Appearance.Dark ? 193 : 203)),0));
                veil.GradientStops.Add(new GradientStop(GlassTint(surfaceColor,accent,Appearance.Dark ? .035 : .025,
                    (byte)(Appearance.Custom ? 208 : Appearance.Dark ? 180 : 162)),1));
                context.DrawRectangle(veil,null,rect);
            }
            image.Freeze();
            var brush = new DrawingBrush(image) { Stretch = Stretch.Fill, Opacity = .975 };
            brush.Freeze(); glass.Background = brush;
            RenderOptions.SetBitmapScalingMode(glass,BitmapScalingMode.HighQuality);
        }

        private static Color GlassTint(Color baseColor, Color tint, double amount, byte alpha)
        {
            return Color.FromArgb(alpha,
                (byte)Math.Round(baseColor.R + (tint.R - baseColor.R) * amount),
                (byte)Math.Round(baseColor.G + (tint.G - baseColor.G) * amount),
                (byte)Math.Round(baseColor.B + (tint.B - baseColor.B) * amount));
        }

        private IntPtr OnSystemAppearanceChanged(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int settingChanged = 0x001A;
            const int themeChanged = 0x031A;
            if (message == 0x0084 && inFlight) { // Keep the Dock clickable inside the temporary animation bounds.
                long pos = lParam.ToInt64();
                var point = new Point(unchecked((short)pos), unchecked((short)(pos >> 16)));
                if (!lastBounds.Contains(point)) { handled = true; return new IntPtr(-1); }
            }
            if (message == settingChanged && !Appearance.Motion) {
                navigation.Cancel();
                if (inFlight) FinishFlight(!closing);
            }
            if ((message == settingChanged || message == themeChanged) && backdrop != null && !LaunchGlass.Enabled()) {
                backdrop = null;
                glass.Background = Appearance.Surface;
            }
            return IntPtr.Zero;
        }

        internal void Open(ScreenInfo screen, string destination, Rect origin, IntPtr dock)
        {
            bool reverse = closing && IsVisible && launchButton == origin && screen.SameLayout(panelScreen);
            if (!reverse && inFlight) FinishFlight(false);
            opening = true; closing = false;
            launchButton = origin;
            launchDock = dock;
            try {
                var hwnd = new WindowInteropHelper(this).EnsureHandle();
                CloseEditor(false);
                selected = 0; category = "All"; folderId = destination; search.Clear();
                BuildPills(); Reposition(screen);
                if (Appearance.Motion && !origin.IsEmpty && !reverse) {
                    PrepareComposition(hwnd, true);
                    if (composition == null) surface.Opacity = 0;
                }
                Show(); PlacePanel(); Activate(); ui.Platform.FocusWindow(hwnd);
                Keyboard.Focus(search); mouseDown = LaunchGlass.ButtonsDown(); outside.Start();
                Animate(true, reverse);
                Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate {
                    if (IsOpen && IsActive) Keyboard.Focus(search);
                }));
            } finally { opening = false; }
        }

        private Rect PanelBounds(ScreenInfo screen)
        {
            var b = ui.Settings.ReplaceTaskbar ? screen.Bounds : screen.WorkArea;
            double s = screen.Scale, gap = Math.Max(1, Math.Round(2*s));
            double topLimit = Math.Max(b.Top, ui.StatusBarBottom(screen)) + Math.Min(12*s,b.Height*.025);
            double dockTop = Math.Min(b.Bottom, Math.Max(b.Top, ui.DockTopPixels));
            if (dockTop <= topLimit + 120*s) dockTop = b.Bottom - 10*s;
            double width = Math.Min(708*s, Math.Max(120*s, b.Width-4*s));
            double height = Math.Min(628*s, Math.Max(95*s, dockTop - gap - topLimit));
            return new Rect(Math.Round(b.Left+(b.Width-width)/2),Math.Round(dockTop-gap-height),
                Math.Round(width),Math.Round(height));
        }

        internal void Reposition(ScreenInfo screen)
        {
            if (screen == null) return;
            var bounds = PanelBounds(screen);
            if (inFlight && !opening && (bounds != lastBounds || !screen.SameLayout(panelScreen))) {
                bool wasClosing = closing;
                FinishFlight(!wasClosing);
                if (wasClosing) return;
            }
            if (!IsVisible || (!lastBounds.IsEmpty && !lastBounds.IntersectsWith(bounds))) {
                var sample = new Rect(bounds.Left+glass.Margin.Left*screen.Scale,bounds.Top+glass.Margin.Top*screen.Scale,
                    Math.Max(1,bounds.Width-(glass.Margin.Left+glass.Margin.Right)*screen.Scale),
                    Math.Max(1,bounds.Height-(glass.Margin.Top+glass.Margin.Bottom)*screen.Scale));
                backdrop = LaunchGlass.Capture(sample,screen.Scale);
                PaintGlass();
            }
            lastBounds = bounds;
            panelScreen = screen;
            PlacePanel();
            double w = bounds.Width/screen.Scale-glass.Margin.Left-glass.Margin.Right;
            double h = bounds.Height/screen.Scale-glass.Margin.Top-glass.Margin.Bottom;
            columns = Math.Max(1, Math.Min(5,(int)((w-35)/97)));
            searchFrame.Width = w >= 470 ? 158 : w >= 360 ? 110 : 76;
            title.FontSize = w >= 450 ? 24 : 19;
            glass.Padding = new Thickness(w < 460 ? 10 : 20, h < 390 ? 2 : 12, w < 460 ? 10 : 20, 5);
            pills.Measure(new Size(Math.Max(50,w-glass.Padding.Left-glass.Padding.Right-4),Double.PositiveInfinity));
            double available = Math.Max(40,h-glass.Padding.Top-glass.Padding.Bottom-58-pills.DesiredSize.Height-30);
            rows = Math.Max(1,Math.Min(4,(int)(available/99)));
            iconSize = Math.Max(18,Math.Min(61,available/rows-40));
            cellWidth = Math.Max(45,(w-glass.Padding.Left-glass.Padding.Right-3)/columns);
            RefreshCatalog();
        }

        internal void Dismiss()
        {
            if (!IsVisible || closing) return;
            navigation.Cancel();
            closing = true; outside.Stop(); Animate(false, true);
        }

        // While zooming, let the layered window cover both endpoints so Windows
        // does not clip the first frames at the edge of the final menu rectangle.
        private void PlacePanel()
        {
            var host = lastBounds;
            if (inFlight && composition == null && !launchButton.IsEmpty) host.Union(launchButton);
            double dpi = panelScreen.Scale;
            surface.Margin = new Thickness((lastBounds.Left - host.Left) / dpi, (lastBounds.Top - host.Top) / dpi,
                (host.Right - lastBounds.Right) / dpi, (host.Bottom - lastBounds.Bottom) / dpi);
            panelScreen.Place(this, host);
        }

        private void PrepareComposition(IntPtr hwnd, bool cloak)
        {
            try {
                if (composition == null) composition = new LaunchComposition(hwnd, FinishAtPointer);
                if (cloak) composition.HideOriginal();
            } catch (ExternalException ex) { DropComposition(ex); }
        }

        internal void PrepareWindow()
        {
            // Create the layered HWND and its compositor while the Dock is idle.
            // The next Open still samples the current monitor, library and desktop.
            PrepareComposition(new WindowInteropHelper(this).EnsureHandle(), false);
        }

        private void DropComposition(ExternalException ex)
        {
            Trace.WriteLine(ex);
            if (composition != null) { composition.Dispose(); composition = null; }
        }

        private Point? FinishAtPointer(Point screenPoint)
        {
            if (!inFlight || closing) return null;
            double scale = CurrentScale, dpi = panelScreen.Scale;
            var client = new Point((screenPoint.X-lastBounds.X-zoom.CenterX*dpi*(1-scale))/scale,
                (screenPoint.Y-lastBounds.Y-zoom.CenterY*dpi*(1-scale))/scale);
            if (client.X < 0 || client.X >= lastBounds.Width || client.Y < 0 || client.Y >= lastBounds.Height) {
                Dismiss();
                return null;
            }
            FinishFlight(true);
            return client;
        }

        private void Animate(bool show, bool fromCurrent)
        {
            double previousScale = CurrentScale, previousAlpha = CurrentAlpha;
            StopFlightFrames();
            if (!Appearance.Motion || launchButton.IsEmpty) { FinishFlight(show); return; }
            double small = Math.Min(.5, Math.Min(launchButton.Width / lastBounds.Width, launchButton.Height / lastBounds.Height));
            // At the initial scale the panel centre coincides with the centre of the clicked button.
            zoom.CenterX = (launchButton.X + launchButton.Width / 2 - lastBounds.X - small * lastBounds.Width / 2)
                / ((1 - small) * panelScreen.Scale);
            zoom.CenterY = (launchButton.Y + launchButton.Height / 2 - lastBounds.Y - small * lastBounds.Height / 2)
                / ((1 - small) * panelScreen.Scale);
            double from = fromCurrent ? previousScale : small, alpha = fromCurrent ? previousAlpha : 0;
            double to = show ? 1 : small;
            inFlight = true;
            double duration = Math.Max(.07, (show ? .30 : .18) * Math.Abs(to-from) / (1-small));
            var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseInOut };
            if (StartComposedFlight(show, from, to, alpha, duration, ease)) return;
            zoom.ScaleX = zoom.ScaleY = from;
            surface.Opacity = alpha;
            PlacePanel();
            if (surface.CacheMode == null) surface.CacheMode = new BitmapCache();
            flightClock = new AnimationFrameClock(Dispatcher, delegate(double elapsed) {
                if (elapsed >= duration) { FinishFlight(show); return; }
                double t = ease.Ease(elapsed / duration);
                zoom.ScaleX = zoom.ScaleY = from + (to - from) * t;
                surface.Opacity = alpha + ((show ? 1 : 0) - alpha) * t;
            });
        }

        private bool StartComposedFlight(bool show, double from, double to, double alpha, double duration, CubicEase ease)
        {
            PrepareComposition(new WindowInteropHelper(this).Handle, false);
            if (composition == null) return false;
            try {
                double dpi = panelScreen.Scale;
                int frames = Math.Max(1, (int)Math.Round(duration * Program.AnimationFrameRate));
                zoom.ScaleX = zoom.ScaleY = surface.Opacity = 1;
                long begin = composition.Play(lastBounds, launchButton, launchDock,
                    new Point(zoom.CenterX*dpi, zoom.CenterY*dpi), frames, ease.Ease, from, to, alpha, show ? 1 : 0);
                startScale = from; endScale = to; startAlpha = alpha; endAlpha = show ? 1 : 0;
                composedProgress = delegate {
                    double elapsed = (Stopwatch.GetTimestamp()-begin)/(double)Stopwatch.Frequency;
                    return ease.Ease(Math.Min(frames, Math.Max(0, Math.Floor(elapsed * Program.AnimationFrameRate - LaunchComposition.SamplePhase)))/frames);
                };
                flightEnd = new DispatcherTimer(DispatcherPriority.Render) {
                    Interval = TimeSpan.FromSeconds((frames+1)/(double)Program.AnimationFrameRate)
                };
                flightEnd.Tick += delegate { FinishFlight(show); };
                flightEnd.Start();
                return true;
            } catch (ExternalException ex) {
                DropComposition(ex);
                return false;
            }
        }

        private void StopFlightFrames()
        {
            if (flightEnd != null) { flightEnd.Stop(); flightEnd = null; }
            composedProgress = null;
            if (flightClock == null) return;
            flightClock.Stop();
            flightClock = null;
        }

        private void FinishFlight(bool show)
        {
            bool composed = composedProgress != null;
            StopFlightFrames();
            if (!show) Hide();
            inFlight = false;
            zoom.ScaleX = zoom.ScaleY = surface.Opacity = 1;
            surface.CacheMode = null;
            if (!composed) PlacePanel();
            if (composition != null) {
                try { composition.Clear(show && composed); }
                catch (ExternalException ex) { DropComposition(ex); }
            }
            if (!show) {
                backdrop = null; glass.Background = Appearance.Surface;
                closing = false; ui.LaunchpadClosed();
            }
        }

        private void OpenActions()
        {
            var menu = Visuals.Menu();
            Action<string,string,Action> add = delegate(string id,string name,Action click) {
                var item = Visuals.MenuItem(name,click); Visuals.Identify(item,"Launchpad.Action."+id,name);
                menu.Items.Add(item);
            };
            AddLibraryActions(menu);
            menu.Items.Add(ui.IconEditModeCommand());
            menu.Items.Add(new Separator());
            add("Explorer",Loc.Get("Explorer"),delegate {
                OpenSystem(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"));
            });
            add("Files",Loc.Get("FindFiles"),SearchFiles);
            add("WindowsSettings",Loc.Get("WindowsSettings"),delegate { OpenSystem("ms-settings:"); });
            add("Power",Loc.Get("Power"),OpenPowerDialog);
            menu.Items.Add(new Separator());
            add("DockSettings",Loc.Get("DockSettingsMenu"),ui.OpenPreferences);
            add("Refresh",Loc.Get("RefreshApps"),delegate { ui.ReloadCatalog(); RefreshCatalog(); });
            var close = Visuals.MenuItem(Loc.Get("Close"),Dismiss); Visuals.Identify(close,"Launchpad.Close",Loc.Get("Close"));
            menu.Items.Add(close);
            menu.PlacementTarget = more; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        }

        private void SearchFiles()
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            OpenSystem("search-ms:query=" + Uri.EscapeDataString(search.Text.Trim()) +
                       "&crumb=location:" + Uri.EscapeDataString(folder));
        }

        private void OpenSystem(string command)
        {
            Dismiss();
            try { Process.Start(new ProcessStartInfo(command) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(Loc.Get("OpenAppError")+"\n\n"+ex.Message,
                "Lunaris Dock",MessageBoxButton.OK,MessageBoxImage.Information); }
        }

        private void OpenPowerDialog()
        {
            Dismiss();
            if (Interlocked.Exchange(ref powerDialogRequested, 1) != 0) return;
            var worker = new Thread(delegate() {
                object shell = null;
                try {
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                    // This shell method displays the native choice dialog. Windows
                    // obtains any action and its confirmation from the user.
                    ((dynamic)shell).ShutdownWindows();
                }
                catch (Exception ex) {
                    string message = ex.Message;
                    if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(delegate {
                        MessageBox.Show(Loc.Get("PowerDialogError") + "\n\n" + message,
                            "Lunaris Dock", MessageBoxButton.OK, MessageBoxImage.Information);
                    }));
                }
                finally {
                    if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
                    Interlocked.Exchange(ref powerDialogRequested, 0);
                }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
        }
    }
}
