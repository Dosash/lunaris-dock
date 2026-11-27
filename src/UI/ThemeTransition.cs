using System;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Lunaris
{
    // Keep input and keyboard focus on the live controls while their old palette fades.
    internal sealed class ThemeTransition : Adorner
    {
        private readonly BitmapSource image;

        private ThemeTransition(FrameworkElement target, BitmapSource image) : base(target)
        {
            this.image = image;
            IsHitTestVisible = false;
            Focusable = false;
        }

        internal static void Begin(Window window)
        {
            if (window == null || !window.IsVisible || !Appearance.Motion) return;
            var target = window.Content as FrameworkElement;
            if (target == null || !target.IsLoaded) return;
            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null) return;
            var rect = new Rect(target.RenderSize);
            if (rect.Width < 1 || rect.Height < 1) return;
            var source = PresentationSource.FromVisual(target);
            if (source == null || source.CompositionTarget == null) return;
            var scale = source.CompositionTarget.TransformToDevice;
            var previous = (layer.GetAdorners(target) ?? new Adorner[0]).OfType<ThemeTransition>().FirstOrDefault();
            var scene = new DrawingVisual();
            using (var dc = scene.RenderOpen())
            {
                var live = new VisualBrush(target) { AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect, Stretch = Stretch.Fill };
                dc.DrawRectangle(live, null, rect);
                if (previous != null)
                {
                    dc.PushOpacity(previous.Opacity);
                    dc.DrawImage(previous.image, new Rect(0,0,previous.image.Width,previous.image.Height));
                    dc.Pop();
                }
            }
            var frame = new RenderTargetBitmap((int)Math.Ceiling(rect.Width * scale.M11),
                (int)Math.Ceiling(rect.Height * scale.M22), 96 * scale.M11, 96 * scale.M22, PixelFormats.Pbgra32);
            frame.Render(scene);
            frame.Freeze();
            if (previous != null) layer.Remove(previous);
            var overlay = new ThemeTransition(target, frame);
            layer.Add(overlay);
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260)) {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            fade.Completed += delegate { if (VisualTreeHelper.GetParent(overlay) == layer) layer.Remove(overlay); };
            overlay.BeginAnimation(OpacityProperty, fade);
        }

        protected override void OnRender(DrawingContext context)
        {
            context.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            context.DrawImage(image, new Rect(0,0,image.Width,image.Height));
            context.Pop();
        }
    }
}
