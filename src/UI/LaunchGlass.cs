using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DrawingFormat = System.Drawing.Imaging.PixelFormat;

namespace Lunaris
{
    internal static class LaunchGlass
    {
        [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr destination, int x, int y, int w, int h,
            IntPtr source, int left, int top, int width, int height, uint rasterOperation);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint pt);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint pt);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        internal static bool Enabled()
        {
            if (SystemParameters.HighContrast) return false;
            try {
                var enabled = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "EnableTransparency", 1);
                return enabled is int && (int)enabled != 0;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        // Downsample only the rectangle about to be covered. No full-size desktop
        // image is created. The retained bitmap is already blurred and stays in RAM.
        internal static BitmapSource Capture(Rect rect, double scale)
        {
            if (!Enabled() || rect.IsEmpty) return null;
            double step = (Appearance.Glass == null ? 10 : 6) * scale;
            int width = Math.Max(2, (int)Math.Ceiling(rect.Width / step));
            int height = Math.Max(2, (int)Math.Ceiling(rect.Height / step));
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            try {
                using (var small = new Bitmap(width, height, DrawingFormat.Format32bppRgb)) {
                    using (var graphics = Graphics.FromImage(small)) {
                        IntPtr dest = graphics.GetHdc();
                        try {
                            SetStretchBltMode(dest, 4); // HALFTONE
                            if (!StretchBlt(dest, 0, 0, width, height, screen, (int)rect.Left, (int)rect.Top,
                                (int)rect.Width, (int)rect.Height, 0x00CC0020)) return null;
                        }
                        finally { graphics.ReleaseHdc(dest); }
                    }
                    var pixels = new byte[width * height * 4];
                    var data = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, DrawingFormat.Format32bppRgb);
                    try {
                        for (int y = 0; y < height; y++)
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * width * 4, width * 4);
                    }
                    finally { small.UnlockBits(data); }
                    var blurred = new byte[pixels.Length];
                    // A weighted neighbourhood blurs the retained, downsampled backdrop.
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                        int blue = 0, green = 0, red = 0, weight = 0;
                        for (int j = Math.Max(0, y - 4); j <= Math.Min(height - 1, y + 4); j++)
                            for (int i = Math.Max(0, x - 4); i <= Math.Min(width - 1, x + 4); i++) {
                                int k = (5 - Math.Abs(i - x)) * (5 - Math.Abs(j - y));
                                int offset = (j * width + i) * 4;
                                blue += k * pixels[offset]; green += k * pixels[offset+1]; red += k * pixels[offset+2];
                                weight += k;
                            }
                        int index = (y * width + x) * 4;
                        blurred[index] = (byte)(blue / weight);
                        blurred[index+1] = (byte)(green / weight);
                        blurred[index+2] = (byte)(red / weight);
                    }
                    var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, blurred, width * 4);
                    image.Freeze(); return image;
                }
            }
            catch (ExternalException) { return null; }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        // Refract the existing blurred sample along the inward normal of the pane's
        // rounded edge. The centre stays flat. No second desktop capture is needed.
        internal static BitmapSource Refract(BitmapSource backdrop)
        {
            int width = backdrop.PixelWidth, height = backdrop.PixelHeight, stride = width * 4;
            var source = new byte[height * stride];
            backdrop.CopyPixels(source, stride, 0);
            var output = (byte[])source.Clone();
            double radius = Math.Min(4, Math.Min(width, height) * .5);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double dx = x + .5 - width * .5, dy = y + .5 - height * .5;
                double ox = Math.Max(0, Math.Abs(dx) - (width * .5 - radius));
                double oy = Math.Max(0, Math.Abs(dy) - (height * .5 - radius));
                double arc = Math.Sqrt(ox * ox + oy * oy);
                if (arc == 0) continue;
                double slope = Math.Min(1, arc / radius);
                double displacement = 1.6 * radius * slope * slope / arc;
                double sx = Math.Min(width - 1, Math.Max(0, x - Math.Sign(dx) * ox * displacement));
                double sy = Math.Min(height - 1, Math.Max(0, y - Math.Sign(dy) * oy * displacement));
                int left = (int)sx, top = (int)sy;
                int right = Math.Min(width - 1, left + 1), bottom = Math.Min(height - 1, top + 1);
                double fx = sx - left, fy = sy - top;
                for (int c = 0; c < 3; c++)
                {
                    double upper = source[top * stride + left * 4 + c] * (1 - fx) + source[top * stride + right * 4 + c] * fx;
                    double lower = source[bottom * stride + left * 4 + c] * (1 - fx) + source[bottom * stride + right * 4 + c] * fx;
                    output[y * stride + x * 4 + c] = (byte)Math.Round(upper * (1 - fy) + lower * fy);
                }
            }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, output, stride);
            result.Freeze();
            return result;
        }

        internal static bool ButtonsDown()
        {
            return GetAsyncKeyState(1) < 0 || GetAsyncKeyState(2) < 0 || GetAsyncKeyState(4) < 0;
        }

        internal static bool Outside(IntPtr hwnd)
        {
            ScreenPoint point; WindowRect rect;
            if (!GetCursorPos(out point) || !GetWindowRect(hwnd, out rect)) return false;
            if (point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom) return false;
            uint owner;
            GetWindowThreadProcessId(WindowFromPoint(point), out owner);
            return owner != GetCurrentProcessId();
        }

        internal static bool LostForeground()
        {
            uint owner; var foreground = GetForegroundWindow();
            return foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out owner) != 0 && owner != GetCurrentProcessId();
        }
    }
}
