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
        private readonly Border[] logoTiles = new Border[4];
        private readonly Button more;
        private readonly Border moreCircle = new Border();
        private readonly WrapPanel pills = new WrapPanel();
        private readonly Grid grid = new Grid();
        private readonly LaunchNavigationMotion navigation;
        private readonly StackPanel pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly DispatcherTimer outside;
        private readonly TranslateTransform slide = new TranslateTransform();
        private readonly List<LibraryItem> results = new List<LibraryItem>();
        private readonly List<Border> frames = new List<Border>();
        private int columns = 5, rows = 4, selected, animationId;
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
            Title = "Lunaris — Пуск";
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
            shadow.IsHitTestVisible = false; glass.BorderThickness = new Thickness(1);
            glass.Padding = new Thickness(19, 12, 19, 6);
            var layers = new Grid();
            layers.Children.Add(layout);
            layers.Children.Add(editor);
            glass.Child = layers;
            surface.RenderTransform = slide;
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
            var header = new Grid { Height = 48, Margin = new Thickness(1,0,1,7) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var logo = new UniformGrid { Rows = 2, Columns = 2, Width = 22, Height = 22,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,10,0) };
            for (int i=0; i<4; i++) {
                logoTiles[i] = new Border { CornerRadius = new CornerRadius(2.2), Margin = new Thickness(1),
                    Opacity = i == 1 ? .65 : .95 };
                logo.Children.Add(logoTiles[i]);
            }
            header.Children.Add(logo);
            navigationLogo = logo;
            backButton = Visuals.Button("Launchpad.Folder.Back", "На уровень выше");
            backButton.Width = 29;
            backButton.Margin = new Thickness(0, 0, 4, 0);
            backButton.Content = Glyph("\uE72B", 17);
            backButton.Click += delegate { NavigateUp(); };
            backButton.AllowDrop = true;
            backButton.DragOver += delegate(object sender, DragEventArgs e) { DragOverFolder(e, ParentFolder()); };
            backButton.Drop += delegate(object sender, DragEventArgs e) { DropInto(e, ParentFolder()); };
            backButton.Visibility = Visibility.Collapsed;
            header.Children.Add(backButton);
            title = Text("Приложения", 24); title.FontFamily = UiFonts.Display; title.FontWeight = FontWeights.Medium;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Margin = new Thickness(0, 0, 8, 0);
            Visuals.Identify(title, "Launchpad.Title", title.Text);
            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            breadcrumb = Text("", 11);
            breadcrumb.TextTrimming = TextTrimming.CharacterEllipsis;
            breadcrumb.Visibility = Visibility.Collapsed;
            Visuals.Identify(breadcrumb, "Launchpad.Folder.Path", "Расположение папки");
            heading.Children.Add(breadcrumb);
            heading.Children.Add(title);
            Grid.SetColumn(heading,1); header.Children.Add(heading);

            searchFrame.Width = 160; searchFrame.Height = 29; searchFrame.VerticalAlignment = VerticalAlignment.Center;
            searchFrame.CornerRadius = new CornerRadius(14); searchFrame.BorderThickness = new Thickness(1);
            var field = new Grid { Margin = new Thickness(9,0,8,0) };
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            field.ColumnDefinitions.Add(new ColumnDefinition());
            searchGlyph = Glyph("\uE721",12); field.Children.Add(searchGlyph);
            placeholder = Text("Поиск",13); Grid.SetColumn(placeholder,1); field.Children.Add(placeholder);
            search.FontFamily = UiFonts.Text; search.FontSize = 13; search.Background = Brushes.Transparent;
            search.VerticalContentAlignment = VerticalAlignment.Center; search.BorderThickness = new Thickness(0);
            search.Padding = new Thickness(0); Visuals.Identify(search,"Search","Поиск приложений");
            search.MaxLength = 1024;
            Grid.SetColumn(search,1); field.Children.Add(search); searchFrame.Child = field;
            Grid.SetColumn(searchFrame,2); header.Children.Add(searchFrame);
            more = Visuals.Button("Launchpad.More","Действия"); more.Width = 35; more.Height = 32;
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
            findFiles = Visuals.Button("Launchpad.FindFiles","Найти файлы в Проводнике");
            findFiles.Content = new TextBlock { Text = "Найти файлы в Проводнике", Margin = new Thickness(12,6,12,6) };
            findFiles.Margin = new Thickness(0,8,0,0);
            findFiles.Click += delegate { SearchFiles(); };
            searchStatus.Children.Add(findFiles);
            addToEmptyFolder = Visuals.Button("Launchpad.Folder.AddEmpty","Добавить приложения");
            addToEmptyFolder.Content = new TextBlock { Text = "Добавить приложения", Margin = new Thickness(12,6,12,6) };
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
                if (down && !mouseDown && IsOpen && LaunchGlass.Outside(new WindowInteropHelper(this).Handle)) Dismiss();
                mouseDown = down;
            };
            Deactivated += delegate {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                    if (IsOpen && !opening && !IsActive && LaunchGlass.LostForeground()) Dismiss();
                }));
            };
            Closed += delegate { outside.Stop(); navigation.Cancel(); ++animationId; };
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
            PaintGlass();
            headerRule.BorderBrush = Translucent(Appearance.Muted,57);
            foreach (var t in logoTiles) t.Background = Appearance.Text;
            title.Foreground = Appearance.Text; searchGlyph.Foreground = Appearance.Muted; placeholder.Foreground = Appearance.Muted;
            breadcrumb.Foreground = Appearance.Muted;
            search.Foreground = Appearance.Text; search.CaretBrush = Appearance.Text; search.SelectionBrush = Appearance.Selection;
            searchFrame.Background = Translucent(Appearance.Panel,143); searchFrame.BorderBrush = Translucent(Appearance.Muted,60);
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
            shadow.Background = Appearance.Surface;
            shadow.Effect = Visuals.Shadow(15,5,Appearance.Dark ? .52 : .34);
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
            if (message == settingChanged && !Appearance.Motion) navigation.Cancel();
            if ((message == settingChanged || message == themeChanged) && backdrop != null && !LaunchGlass.Enabled()) {
                backdrop = null;
                glass.Background = Appearance.Surface;
            }
            return IntPtr.Zero;
        }

        internal void Open() { Open(ui.ActiveMonitor); }

        internal void Open(ScreenInfo screen)
        {
            opening = true; closing = false; ++animationId;
            try {
                var hwnd = new WindowInteropHelper(this).EnsureHandle();
                CloseEditor(false);
                selected = 0; category = "All"; folderId = LaunchLibrary.Root; search.Clear();
                BuildPills(); Reposition(screen);
                Show(); screen.Place(this, PanelBounds(screen)); Activate(); ui.Platform.FocusWindow(hwnd);
                Keyboard.Focus(search); mouseDown = LaunchGlass.ButtonsDown(); outside.Start();
                Animate(true);
                Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate {
                    if (IsOpen && IsActive) Keyboard.Focus(search);
                }));
            } finally { opening = false; }
        }

        private Rect PanelBounds(ScreenInfo screen)
        {
            var b = ui.Settings.ReplaceTaskbar ? screen.Bounds : screen.WorkArea;
            double s = screen.Scale, gap = Math.Max(1, Math.Round(2*s));
            double topLimit = b.Top + Math.Min(12*s,b.Height*.025);
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
            if (!IsVisible || (!lastBounds.IsEmpty && !lastBounds.IntersectsWith(bounds))) {
                var sample = new Rect(bounds.Left+glass.Margin.Left*screen.Scale,bounds.Top+glass.Margin.Top*screen.Scale,
                    Math.Max(1,bounds.Width-(glass.Margin.Left+glass.Margin.Right)*screen.Scale),
                    Math.Max(1,bounds.Height-(glass.Margin.Top+glass.Margin.Bottom)*screen.Scale));
                backdrop = LaunchGlass.Capture(sample,screen.Scale);
                PaintGlass();
            }
            lastBounds = bounds;
            screen.Place(this,bounds);
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
            closing = true; outside.Stop(); Animate(false);
        }

        private void Animate(bool show)
        {
            int token = ++animationId;
            surface.BeginAnimation(OpacityProperty,null); slide.BeginAnimation(TranslateTransform.YProperty,null);
            Action done = delegate {
                if (!show && token == animationId && IsVisible) {
                    Hide(); backdrop = null; glass.Background = Appearance.Surface;
                    closing = false; ui.LaunchpadClosed();
                }
            };
            if (!Appearance.Motion) { done(); return; }
            var ms = TimeSpan.FromMilliseconds(show ? 145 : 85);
            var fade = new DoubleAnimation(show ? .15 : 1,show ? 1 : .08,ms);
            fade.Completed += delegate { done(); };
            surface.BeginAnimation(OpacityProperty,fade);
            slide.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(show ? 7 : 0,show ? 0 : 5,ms) {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        private void OpenActions()
        {
            var menu = Visuals.Menu();
            Action<string,string,Action> add = delegate(string id,string name,Action click) {
                var item = Visuals.MenuItem(name,click); Visuals.Identify(item,"Launchpad.Action."+id,name);
                menu.Items.Add(item);
            };
            AddLibraryActions(menu);
            menu.Items.Add(new Separator());
            add("Explorer","Проводник",delegate {
                OpenSystem(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"));
            });
            add("Files","Найти файлы в Проводнике",SearchFiles);
            add("WindowsSettings","Параметры Windows",delegate { OpenSystem("ms-settings:"); });
            add("Power","Питание и выход…",OpenPowerDialog);
            menu.Items.Add(new Separator());
            add("DockSettings","Настройки Dock…",ui.OpenPreferences);
            add("Refresh","Обновить приложения",delegate { ui.ReloadCatalog(); RefreshCatalog(); });
            var close = Visuals.MenuItem("Закрыть",Dismiss); Visuals.Identify(close,"Launchpad.Close","Закрыть");
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
            catch (Exception ex) { MessageBox.Show("Не удалось открыть приложение.\n\n"+ex.Message,
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
                        MessageBox.Show("Не удалось открыть системный диалог питания.\n\n" + message,
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
