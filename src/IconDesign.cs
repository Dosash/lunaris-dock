using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lunaris
{
    [DataContract]
    public sealed class IconDesign
    {
        [DataMember] public string Key;
        [DataMember] public string Shape;
        [DataMember] public string BaseColor;
        [DataMember] public string InkColor;
        [DataMember] public string Foreground;
        [DataMember] public string Glyph;
        [DataMember] public string Text;
        [DataMember] public int Size;
        [DataMember] public int OffsetX;
        [DataMember] public int OffsetY;
        [DataMember(EmitDefaultValue=false)] public string ImagePng;
        private string cachedPng;
        private BitmapSource cachedImage;

        internal static IconDesign Default(string key, bool folder)
        {
            return new IconDesign { Key = key, Shape = folder ? "folder" : "tile", BaseColor = "#6786DC",
                InkColor = "#FFFFFF", Foreground = "symbol", Glyph = "star", Text = "", Size = 52 };
        }

        internal IconDesign Copy() { return (IconDesign)MemberwiseClone(); }

        internal static List<IconDesign> NormalizeList(List<IconDesign> value)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = (value ?? new List<IconDesign>()).FindAll(d => d != null && !String.IsNullOrWhiteSpace(d.Key) && keys.Add(d.Key));
            foreach (var item in result)
            {
                if (item.Shape != "folder" && item.Shape != "none") item.Shape = "tile";
                if (item.Foreground != "image" && item.Foreground != "text") item.Foreground = "symbol";
                item.BaseColor = UserSettings.NormalizeColor(item.BaseColor, "#6786DC");
                item.InkColor = UserSettings.NormalizeColor(item.InkColor, "#FFFFFF");
                if (!IconGlyphs.Contains(item.Glyph)) item.Glyph = "star";
                item.Text = ShortText(item.Text);
                item.Size = Math.Max(20, Math.Min(100, item.Size == 0 ? 52 : item.Size));
                item.OffsetX = Math.Max(-35, Math.Min(35, item.OffsetX));
                item.OffsetY = Math.Max(-35, Math.Min(35, item.OffsetY));
                if (item.ImagePng != null && item.ImagePng.Length > 512 * 1024) item.ImagePng = null;
                if (item.Foreground == "image" && String.IsNullOrEmpty(item.ImagePng)) item.Foreground = "symbol";
            }
            return result;
        }

        internal static string ShortText(string value)
        {
            string line = (value ?? "").Replace("\r", "").Replace("\n", "");
            var info = new StringInfo(line);
            return info.LengthInTextElements > 8 ? info.SubstringByTextElements(0, 8) : line;
        }

        // Freeze the selected frame into the profile. The original file can be moved or deleted.
        internal static string ImportImage(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException();
                BitmapDecoder decoder;
                string extension = Path.GetExtension(path);
                if (String.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase))
                    decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                else if (String.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                    decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                else throw new InvalidDataException();
                var frame = decoder.Frames.Where(f => f.PixelWidth > 0 && f.PixelHeight > 0 &&
                        (long)f.PixelWidth * f.PixelHeight <= 16777216)
                    .OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).ThenByDescending(f => f.Format.BitsPerPixel).FirstOrDefault();
                if (frame == null) throw new InvalidDataException();
                double factor = Math.Min(1, 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
                var size = new Size(Math.Max(1, Math.Round(frame.PixelWidth * factor)), Math.Max(1, Math.Round(frame.PixelHeight * factor)));
                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                using (var dc = visual.RenderOpen()) dc.DrawImage(frame, new Rect(size));
                var output = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                output.Render(visual);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(output));
                using (var encoded = new MemoryStream())
                {
                    png.Save(encoded); return Convert.ToBase64String(encoded.ToArray());
                }
            }
        }

        private BitmapSource Image()
        {
            if (ImagePng == cachedPng) return cachedImage;
            cachedPng = ImagePng; cachedImage = null;
            if (String.IsNullOrEmpty(ImagePng) || ImagePng.Length > 512 * 1024) return null;
            try
            {
                using (var stream = new MemoryStream(Convert.FromBase64String(ImagePng), false))
                {
                    var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    if (frame.PixelWidth > 256 || frame.PixelHeight > 256) return null;
                    var copy = new WriteableBitmap(frame);
                    copy.Freeze(); cachedImage = copy;
                }
            }
            catch (FormatException) { }
            catch (NotSupportedException) { }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            return cachedImage;
        }

        internal UIElement Render(double size)
        {
            var ink = Visuals.Brush(InkColor);
            Color color = Visuals.Brush(BaseColor).Color;
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                var bounds = new Rect(0, 0, 100, 100);
                dc.DrawRectangle(Brushes.Transparent, null, bounds);
                dc.PushClip(new RectangleGeometry(bounds));
                var gradient = new LinearGradientBrush(Visuals.Mix(color, Colors.White, .22), color, 75);
                if (Shape == "folder")
                {
                    dc.DrawGeometry(new SolidColorBrush(Visuals.Mix(color, Colors.Black, .20)), null, FolderBack);
                    dc.DrawGeometry(gradient, null, FolderFront);
                }
                else if (Shape == "tile") dc.DrawRoundedRectangle(gradient, null, new Rect(1, 1, 98, 98), 22, 22);

                double left = (100 - Size) / 2.0 + OffsetX;
                double top = (100 - Size) / 2.0 + OffsetY + (Shape == "folder" ? 7 : 0);
                var area = new Rect(left, top, Size, Size);
                var image = Foreground == "image" ? Image() : null;
                if (image != null)
                {
                    double scale = Size / (double)Math.Max(image.PixelWidth, image.PixelHeight);
                    double w = image.PixelWidth * scale, h = image.PixelHeight * scale;
                    dc.DrawImage(image, new Rect(left + (Size-w)/2, top + (Size-h)/2, w, h));
                }
                else if (Foreground == "text")
                {
                    var text = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(UiFonts.Text, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 64, ink, 1);
                    if (text.Width > 0)
                    {
                        double scale = Math.Min(area.Width / text.Width, area.Height / text.Height);
                        dc.PushTransform(new TranslateTransform(left + (Size - text.Width * scale)/2, top + (Size - text.Height * scale)/2));
                        dc.PushTransform(new ScaleTransform(scale, scale));
                        dc.DrawText(text, new Point()); dc.Pop(); dc.Pop();
                    }
                }
                else
                {
                    dc.PushTransform(new TranslateTransform(left, top));
                    dc.PushTransform(new ScaleTransform(Size / 24.0, Size / 24.0));
                    var pen = new Pen(ink, 1.85) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    dc.DrawGeometry(null, pen, IconGlyphs.Get(Glyph));
                    dc.Pop(); dc.Pop();
                }
                dc.Pop();
            }
            var source = new DrawingImage(group); source.Freeze();
            return new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        }

        private static Geometry FrozenPath(string path)
        {
            var result = Geometry.Parse(path); result.Freeze(); return result;
        }

        private static readonly Geometry FolderBack = FrozenPath("M3 24Q3 15 12 15H37Q40 15 43 19L49 25H87Q96 25 96 34V73Q96 84 86 84H13Q3 84 3 74Z");
        private static readonly Geometry FolderFront = FrozenPath("M3 39Q3 32 11 32H90Q98 32 97 40L93 78Q92 87 83 87H15Q7 87 6 78Z");
    }
}
