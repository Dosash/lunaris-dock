using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Lunaris
{
    internal enum LaunchMove { NextPage, PreviousPage, EnterFolder, LeaveFolder }

    internal sealed class LaunchNavigationMotion
    {
        internal readonly Grid Viewport = new Grid { Background = Brushes.Transparent, ClipToBounds = true };
        internal readonly Grid Content = new Grid();
        private readonly Border outgoing = new Border { IsHitTestVisible = false, Focusable = false, Visibility = Visibility.Collapsed };
        private readonly Func<bool> enabled;
        private int revision;

        internal LaunchNavigationMotion(Func<bool> enabled)
        {
            this.enabled = enabled;
            Viewport.Children.Add(outgoing);
            Viewport.Children.Add(Content);
        }

        internal bool Active { get { return outgoing.Background != null; } }

        internal void Cancel()
        {
            if (!Active) return;
            ++revision;
            outgoing.BeginAnimation(UIElement.OpacityProperty, null);
            Content.BeginAnimation(UIElement.OpacityProperty, null);
            Content.Opacity = 1;
            outgoing.RenderTransform = Content.RenderTransform = Transform.Identity;
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Background = null;
        }

        internal void Change(LaunchMove move, Point origin, Action update)
        {
            // If input reverses mid-flight, start at the visible composite, including
            // its current offsets. The old frame contains pixels only, never controls.
            var frame = enabled() ? Capture() : null;
            Cancel();
            update();
            if (frame == null) return;
            outgoing.Background = frame;
            outgoing.Visibility = Visibility.Visible;
            int token = ++revision;
            bool paging = move == LaunchMove.NextPage || move == LaunchMove.PreviousPage;
            var duration = TimeSpan.FromMilliseconds(paging ? 215 : 225);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (paging)
            {
                double offset = Viewport.ActualWidth * (move == LaunchMove.NextPage ? 1 : -1);
                Slide(outgoing, 0, -offset, duration, ease);
                Slide(Content, offset, 0, duration, ease);
            }
            else
            {
                bool entering = move == LaunchMove.EnterFolder;
                Zoom(outgoing, origin, 1, entering ? 1.12 : .89, duration, ease);
                Zoom(Content, origin, entering ? .89 : 1.12, 1, duration, ease);
                Content.Opacity = 0;
                Content.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, duration, ease));
            }
            var exit = Tween(1, paging ? .70 : 0, duration, ease);
            exit.Completed += delegate { if (revision == token) Cancel(); };
            outgoing.BeginAnimation(UIElement.OpacityProperty, exit);
        }

        private static DoubleAnimation Tween(double from, double to, TimeSpan duration, IEasingFunction ease)
        {
            return new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        }

        private static void Slide(UIElement element, double from, double to, TimeSpan duration, IEasingFunction ease)
        {
            var motion = new TranslateTransform(from, 0);
            element.RenderTransform = motion;
            motion.BeginAnimation(TranslateTransform.XProperty, Tween(from, to, duration, ease));
        }

        private static void Zoom(FrameworkElement element, Point origin, double from, double to, TimeSpan duration, IEasingFunction ease)
        {
            var motion = new ScaleTransform(from, from);
            element.RenderTransformOrigin = origin;
            element.RenderTransform = motion;
            var scale = Tween(from, to, duration, ease);
            motion.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            motion.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }

        private ImageBrush Capture()
        {
            // A second input may arrive before WPF arranges the previous destination.
            Viewport.UpdateLayout();
            var rect = new Rect(Viewport.RenderSize);
            if (rect.Width < 1 || rect.Height < 1) return null;
            var source = PresentationSource.FromVisual(Viewport);
            double factor = source == null || source.CompositionTarget == null ? 1 :
                source.CompositionTarget.TransformToDevice.M11;
            // The transient page bitmap stays under ~1 megapixel even at high DPI.
            factor = Math.Min(factor, Math.Sqrt(1000000.0 / (rect.Width * rect.Height)));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var live = new VisualBrush(Viewport) {
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect, Stretch = Stretch.Fill
                };
                dc.DrawRectangle(live, null, rect);
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(rect.Width * factor),
                (int)Math.Ceiling(rect.Height * factor), 96 * factor, 96 * factor, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }
    }
}
