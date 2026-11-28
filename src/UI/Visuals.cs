using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Shapes;

namespace Lunaris
{
    internal static class Visuals
    {
        private sealed class IconContrast
        {
            internal readonly bool NeedsLightBacking;
            internal readonly bool NeedsDarkBacking;

            internal IconContrast(BitmapSource source)
            {
                // Sample the present artwork, regardless of app name or image format.
                double scale = Math.Min(1.0, 32.0 / Math.Max(source.PixelWidth, source.PixelHeight));
                BitmapSource thumbnail = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
                var rgba = new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
                int count = rgba.PixelWidth * rgba.PixelHeight;
                var pixels = new byte[count * 4];
                rgba.CopyPixels(pixels, rgba.PixelWidth * 4, 0);
                int clear = 0, visible = 0, nearBlack = 0, nearWhite = 0;
                for (int i = 0; i < pixels.Length; i += 4) {
                    if (pixels[i + 3] < 32) clear++;
                    if (pixels[i + 3] < 128) continue;
                    visible++;
                    if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) < 68) nearBlack++;
                    if (Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) > 198) nearWhite++;
                }
                NeedsLightBacking = clear > count / 10 && visible > 0 && nearBlack >= visible * .92;
                NeedsDarkBacking = clear > count / 10 && visible > 0 && nearWhite >= visible * .92;
            }
        }

        private static readonly ConditionalWeakTable<BitmapSource, IconContrast> IconContrasts =
            new ConditionalWeakTable<BitmapSource, IconContrast>();

        internal static SolidColorBrush Brush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        internal static Color Mix(Color a, Color b, double amount)
        {
            return Color.FromRgb((byte)Math.Round(a.R+(b.R-a.R)*amount), (byte)Math.Round(a.G+(b.G-a.G)*amount),
                                 (byte)Math.Round(a.B+(b.B-a.B)*amount));
        }

        internal static TextBlock Label(string value, double size, string color)
        {
            return new TextBlock { Text = value, FontFamily = size >= 20 ? UiFonts.Display : UiFonts.Text,
                FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };
        }

        internal static void Identify(DependencyObject element, string id, string name)
        {
            AutomationProperties.SetAutomationId(element, id);
            AutomationProperties.SetName(element, name);
        }

        internal static Button Button(string id, string name)
        {
            var button = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = Tip(name),
                FontFamily = UiFonts.Text,
                VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            border.AppendChild(content);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            Identify(button, id, name);
            ToolTipService.SetInitialShowDelay(button, 450);
            ToolTipService.SetBetweenShowDelay(button, 75);
            return button;
        }

        internal static ToolTip Tip(string value)
        {
            var tip = new ToolTip { Content = value, FontFamily = UiFonts.Text, Padding = new Thickness(9,5,9,5) };
            tip.Opened += delegate {
                tip.Background = Appearance.Panel; tip.Foreground = Appearance.Text; tip.BorderBrush = Appearance.Border;
            };
            return tip;
        }

        internal static ContextMenu Menu()
        {
            var menu = new ContextMenu { FontFamily = UiFonts.Text, Padding = new Thickness(3), FontSize = 13 };
            menu.Opened += delegate {
                menu.Background = Appearance.Panel; menu.Foreground = Appearance.Text; menu.BorderBrush = Appearance.Border;
                menu.Resources[SystemColors.MenuBrushKey] = Appearance.Panel;
                menu.Resources[SystemColors.MenuTextBrushKey] = Appearance.Text;
                menu.Resources[SystemColors.HighlightBrushKey] = Appearance.Selection;
                menu.Resources[SystemColors.HighlightTextBrushKey] = Appearance.Text;
                menu.Resources[SystemColors.ControlTextBrushKey] = Appearance.Text;
            };
            return menu;
        }

        internal static UIElement FolderIcon(double size, LibraryItem folder, IEnumerable<LibraryItem> contents,
            Func<AppEntry,double,UIElement> childArt = null)
        {
            var icon = new Grid { Width = size, Height = size, IsHitTestVisible = false };
            string top = folder == null ? "#FFE3A0" : folder.IsSystem ? "#95DDF9" : "#ADBCFC";
            string bottom = folder == null ? "#DFAB43" : folder.IsSystem ? "#449EDB" : "#788AD7";
            icon.Children.Add(new Border { Width = size * .56, Height = size * .30,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1,size*.10,0,0), CornerRadius = new CornerRadius(size*.09),
                Background = Brush(bottom) });
            var preview = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(size*.12,size*.07,size*.12,size*.07) };
            if (contents != null)
                foreach (var child in contents.Where(i => !i.IsFolder).Take(4))
                    preview.Children.Add(childArt == null ? Icon(child.App, size*.25) : childArt(child.App, size*.25));
            icon.Children.Add(new Border { Margin = new Thickness(0,size*.24,0,size*.07),
                Background = new LinearGradientBrush(Brush(top).Color,Brush(bottom).Color,90),
                BorderBrush = Brush("#50FFFFFF"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(size*.10), Child = preview });
            return icon;
        }

        internal static UIElement Icon(AppEntry app, double size)
        {
            if (app != null && app.DirectoryPath != null) return FolderIcon(size, null, null);
            if (app != null && app.Icon != null)
            {
                var image = new Image { Source = app.Icon, Width = size * .86, Height = size * .86, Stretch = Stretch.Uniform,
                    HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var bitmap = app.Icon as BitmapSource;
                Color backing=Mix(Appearance.Surface.Color,Appearance.Panel.Color,.58);
                if (bitmap!=null) {
                    var contrast=IconContrasts.GetValue(bitmap, source=>new IconContrast(source));
                    double brightness=(.2126*backing.R+.7152*backing.G+.0722*backing.B)/255;
                    if(contrast.NeedsLightBacking && brightness<.60)
                        backing=Mix(backing,Colors.White,(.60-brightness)/(1-brightness));
                    if(contrast.NeedsDarkBacking && brightness>.40)
                        backing=Mix(backing,Colors.Black,1-.40/brightness);
                }
                return new Border { Width=size, Height=size, Background=new SolidColorBrush(backing),
                    BorderBrush=new SolidColorBrush(Mix(backing,Appearance.Muted.Color,.26)),
                    BorderThickness=new Thickness(Math.Min(.75,size*.035)), CornerRadius=new CornerRadius(size*.22),
                    Child=image, IsHitTestVisible=false };
            }
            var text = Label(app == null || String.IsNullOrWhiteSpace(app.Name) ? "•" :
                System.Globalization.StringInfo.GetNextTextElement(app.Name.Trim()), size * .52, "#F7FAFF");
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.Foreground=Appearance.Text;
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * .22),
                Background = Appearance.Selection, BorderBrush=Appearance.Border, BorderThickness=new Thickness(.75),
                Child = text, IsHitTestVisible = false };
        }

        private static readonly Lazy<BitmapSource> LaunchArtwork = new Lazy<BitmapSource>(() =>
        {
            using (var stream = typeof(Visuals).Assembly.GetManifestResourceStream("Lunaris.LunarisDock.ico"))
            {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var source = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
                source.Freeze();
                return source;
            }
        });

        internal static Image LaunchIcon(double size)
        {
            var image = new Image { Source = LaunchArtwork.Value, Width = size, Height = size, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        internal static MenuItem MenuItem(string title, Action action)
        {
            var item = new MenuItem { Header = title, FontFamily = UiFonts.Text, FontSize = 13,
                Padding = new Thickness(9, 5, 15, 5) };
            item.Click += delegate { action(); };
            return item;
        }

        internal static void Animate(Animatable target, DependencyProperty property, double to, int ms)
        {
            if (!Appearance.Motion) {
                target.BeginAnimation(property, null);
                target.SetValue(property, to);
                return;
            }
            var animation = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        internal static DropShadowEffect Shadow(double blur, double depth, double opacity)
        {
            return new DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Direction = 270,
                Opacity = opacity, Color = Colors.Black };
        }

        internal static Rect Primary(Window window, bool work)
        {
            var monitor = new MonitorInfo { size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(MonitorFromPoint(new NativePoint(), 1), ref monitor))
                return work ? SystemParameters.WorkArea : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            var source = PresentationSource.FromVisual(window);
            var convert = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice :
                new Matrix(96.0 / VisualTreeHelper.GetDpi(window).PixelsPerInchX, 0, 0, 96.0 / VisualTreeHelper.GetDpi(window).PixelsPerInchY, 0, 0);
            var r = work ? monitor.work : monitor.screen;
            return new Rect(convert.Transform(new Point(r.left, r.top)), convert.Transform(new Point(r.right, r.bottom)));
        }

        internal static void ToolWindow(Window window, bool noActivate)
        {
            var h = new WindowInteropHelper(window).Handle;
            var value = GetWindowLong(h, -20);
            SetWindowLong(h, -20, value | 0x80 | (noActivate ? 0x08000000 : 0));
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int size; public NativeRect screen, work; public uint flags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}
