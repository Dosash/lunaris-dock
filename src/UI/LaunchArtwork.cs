using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lunaris
{
    internal static class LaunchArtwork
    {
        private static BitmapSource cached;
        private static Color lastPanel, lastAccent, lastText;
        private static bool lastHighContrast;

        internal static BitmapSource ForTheme()
        {
            bool hc = SystemParameters.HighContrast;
            Color panel = hc ? SystemColors.WindowColor : Appearance.Panel.Color;
            Color accent = hc ? SystemColors.HighlightColor : Appearance.Accent.Color;
            Color text = hc ? SystemColors.HighlightTextColor : Appearance.Text.Color;
            if (cached != null && panel == lastPanel && accent == lastAccent && text == lastText && hc == lastHighContrast)
                return cached;

            // Recolour the original three columns of tiles, preserving the glyphs,
            // bevel and alpha channel at every size used in the Dock and Start.
            using (var stream = typeof(LaunchArtwork).Assembly.GetManifestResourceStream("Lunaris.LunarisDock.ico")) {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                var source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
                var pixels = new byte[height * stride];
                source.CopyPixels(pixels, stride, 0);
                Color plateLow = hc ? panel : Visuals.Mix(panel, accent, .15);
                Color plateHigh = hc ? panel : Visuals.Mix(Visuals.Mix(panel, accent, .32), text, .17);
                Color tileLow = hc ? accent : Visuals.Mix(accent, Colors.Black, .12);
                Color tileHigh = hc ? accent : Visuals.Mix(accent, Colors.White, .28);
                var mid = Visuals.Mix(tileLow, tileHigh, .5);
                bool lightTile = .2126*mid.ScR + .7152*mid.ScG + .0722*mid.ScB > .179;
                Color ink = hc ? text : (lightTile ? Colors.Black : Colors.White);
                for (int y = 0; y < height; y++) {
                    double dy = DistanceToTile((y + .5)*256/height, 68);
                    for (int x = 0; x < width; x++) {
                        int i = y*stride + x*4;
                        if (pixels[i+3] == 0) continue;
                        int b = pixels[i], g = pixels[i+1], r = pixels[i+2];
                        double light = (.30*r + .59*g + .11*b) / 255;
                        Color tone = Visuals.Mix(plateLow, plateHigh, Clamp((light-.66)/.30));
                        double dx = DistanceToTile((x + .5)*256/width, 65);
                        if (dx < 27 && dy < 27) {
                            int minimum = Math.Min(r, Math.Min(g,b));
                            var tile = Visuals.Mix(tileLow, tileHigh, Clamp((light-.2)/.63));
                            // The white pictograms lie inside the coloured tiles.
                            // At their outer edges chroma gives the antialiased coverage.
                            if (dx <= 18 && dy <= 18)
                                tone = Visuals.Mix(tile, ink, Clamp((minimum-155)/100.0));
                            else {
                                int chroma = Math.Max(r, Math.Max(g,b)) - minimum;
                                tone = Visuals.Mix(tone, tile, Clamp((chroma-28)/60.0));
                            }
                        }
                        pixels[i] = tone.B; pixels[i+1] = tone.G; pixels[i+2] = tone.R;
                    }
                }
                var image = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
                image.Freeze();
                lastPanel = panel; lastAccent = accent; lastText = text; lastHighContrast = hc;
                return cached = image;
            }
        }

        private static double Clamp(double value) { return Math.Max(0, Math.Min(1, value)); }

        // The original 256 px artwork places nine 50 px tiles on a 62 px grid.
        private static double DistanceToTile(double position, double first)
        {
            double index = Math.Max(0, Math.Min(2, Math.Round((position-first)/62)));
            return Math.Abs(position - (first + index*62));
        }
    }
}
