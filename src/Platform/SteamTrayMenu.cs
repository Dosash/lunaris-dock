using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Lunaris
{
    // Steam renders its tray menu in a separate SDL window. On scaled displays
    // its final HWND can be displaced even when the shell click is in physical pixels.
    internal sealed class SteamTrayMenu
    {
        private DispatcherTimer pending;

        internal void Cancel()
        {
            if (pending != null) { pending.Stop(); pending = null; }
        }

        internal void Watch(uint trayPid, Int32Rect button, Rect workArea, double clickX)
        {
            Cancel();
            string owner = ProcessName(trayPid);
            if (owner != "steam" && owner != "steamwebhelper") return;
            var elapsed = Stopwatch.StartNew();
            var previous = GetForegroundWindow();
            pending = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            pending.Tick += delegate {
                if (elapsed.ElapsedMilliseconds > 900) { Cancel(); return; }
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero || hwnd == previous || !IsWindowVisible(hwnd)) return;
                uint pid;
                if (GetWindowThreadProcessId(hwnd, out pid) == 0 ||
                    (owner == "steamwebhelper" && pid != trayPid)) return;
                var name = new StringBuilder(64);
                if (GetClassName(hwnd, name, name.Capacity) == 0 || name.ToString() != "SDL_app" ||
                    GetWindowText(hwnd, name, name.Capacity) == 0 || name.ToString() != "Menu" ||
                    GetWindow(hwnd, 4) == IntPtr.Zero || ProcessName(pid) != "steamwebhelper") return;
                NativeRect menu;
                if (!GetWindowRect(hwnd, out menu) || menu.Right <= menu.Left || menu.Bottom <= menu.Top) return;
                Cancel();
                var target = Position(button, workArea, menu.Right - menu.Left, menu.Bottom - menu.Top, clickX);
                if (menu.Left != target.X || menu.Top != target.Y)
                    SetWindowPos(hwnd, IntPtr.Zero, (int)target.X, (int)target.Y, 0, 0,
                        0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x4000); // async, keep size, z-order and focus
            };
            pending.Start();
        }

        internal static Point Position(Int32Rect button, Rect area, int width, int height, double clickX)
        {
            double x = clickX + width <= area.Right ? clickX : clickX - width;
            return new Point(Math.Max(area.Left, Math.Min(x, area.Right - width)),
                Math.Max(area.Top, Math.Min(button.Y + button.Height, area.Bottom - height)));
        }

        internal static string ProcessName(uint pid)
        {
            try { using (var p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant(); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    }
}
