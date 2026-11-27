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
            int width = Math.Max(2, (int)Math.Ceiling(rect.Width / (10 * scale)));
            int height = Math.Max(2, (int)Math.Ceiling(rect.Height / (10 * scale)));
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
                    // Eight-by-eight neighborhoods blend roughly 80 DIP of the real backdrop.
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
