using System.Windows;
using System.Windows.Media;

namespace Lunaris
{
    // Shared, immutable brushes for WPF's per-pixel transparent windows.
    internal sealed class LiquidGlassMaterial
    {
        internal readonly Brush Dock, Pane, Group, Rim;

        internal LiquidGlassMaterial(Color surface, Color panel, Color accent, bool dark)
        {
            Color highlight = Visuals.Mix(surface, Colors.White, dark ? .26 : .84);
            Color tint = Visuals.Mix(surface, accent, dark ? .16 : .10);
            Dock = Coat(highlight, surface, tint, 124);
            // Keep enough tinted material under text even over a contrasting window.
            Pane = Coat(highlight, surface, tint, dark ? (byte)197 : (byte)210);
            Group = new LinearGradientBrush(WithAlpha(panel, 178), WithAlpha(panel, 142), 90);
            Group.Freeze();

            var rim = new LinearGradientBrush { StartPoint = new Point(.18, 0), EndPoint = new Point(.82, 1) };
            rim.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, 244), 0));
            rim.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, 104), .28));
            rim.GradientStops.Add(new GradientStop(WithAlpha(Visuals.Mix(surface, Colors.Black, .36), 150), .54));
            rim.GradientStops.Add(new GradientStop(WithAlpha(Visuals.Mix(accent, Colors.White, .66), 212), .88));
            rim.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, 236), 1));
            rim.Freeze();
            Rim = rim;
        }

        private static Brush Coat(Color highlight, Color surface, Color tint, byte bodyAlpha)
        {
            var body = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(.06, 1) };
            body.GradientStops.Add(new GradientStop(WithAlpha(highlight, 227), 0));
            body.GradientStops.Add(new GradientStop(WithAlpha(highlight, bodyAlpha), .10));
            body.GradientStops.Add(new GradientStop(WithAlpha(surface, bodyAlpha), .46));
            body.GradientStops.Add(new GradientStop(WithAlpha(tint, bodyAlpha), .88));
            body.GradientStops.Add(new GradientStop(WithAlpha(highlight, 210), 1));

            // Grazing light pools just inside two opposite edges of the curved surface.
            var reflection = new RadialGradientBrush(WithAlpha(Colors.White, 90), WithAlpha(Colors.White, 0)) {
                Center = new Point(.18, -.05), GradientOrigin = new Point(.18, -.05), RadiusX = .53, RadiusY = .29
            };
            var caustic = new RadialGradientBrush(WithAlpha(highlight, 112), WithAlpha(highlight, 0)) {
                Center = new Point(.82, 1.07), GradientOrigin = new Point(.82, 1.07), RadiusX = .43, RadiusY = .20
            };
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                var bounds = new Rect(0, 0, 1, 1);
                dc.DrawRectangle(body, null, bounds);
                dc.DrawRectangle(reflection, null, bounds);
                dc.DrawRectangle(caustic, null, bounds);
            }
            var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            color.A = alpha;
            return color;
        }
    }
}
