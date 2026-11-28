using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lunaris
{
    internal sealed class ScreenInfo
    {
        internal readonly string Id, Label;
        internal readonly bool Primary;
        internal readonly Rect Bounds, WorkArea;
        internal readonly double Scale;
        internal IntPtr Handle;

        internal ScreenInfo(string id, string label, bool primary, Rect bounds, Rect workArea, double scale)
        {
            Id = id; Label = label; Primary = primary; Bounds = bounds; WorkArea = workArea; Scale = scale;
        }

        internal bool SameLayout(ScreenInfo other)
        {
            return other != null && Id == other.Id && Label == other.Label && Primary == other.Primary &&
                Bounds == other.Bounds && WorkArea == other.WorkArea && Scale == other.Scale && Handle == other.Handle;
        }

        internal bool Contains(Point point)
        {
            return point.X >= Bounds.Left && point.X < Bounds.Right && point.Y >= Bounds.Top && point.Y < Bounds.Bottom;
        }

        internal bool AtBottom(Point previous, Point current)
        {
            double edge = Bounds.Bottom;
            if (current.X >= Bounds.Left && current.X < Bounds.Right &&
                current.Y >= edge - Math.Ceiling(3 * Scale) && current.Y < edge + 6) return true;
            // There is no physical pointer stop when the next screen is directly below.
            // Interpolate the seam crossing even if a fast pointer traversed it between polls.
            if (!Contains(previous) || current.Y < edge || current.Y <= previous.Y) return false;
            double x = previous.X + (current.X - previous.X) * (edge - previous.Y) / (current.Y - previous.Y);
            return x >= Bounds.Left && x < Bounds.Right;
        }

        internal void Place(Window window, Rect physicalBounds)
        {
            int left = (int)Math.Round(physicalBounds.Left), top = (int)Math.Round(physicalBounds.Top);
            int width = Math.Max(1, (int)Math.Round(physicalBounds.Right) - left);
            int height = Math.Max(1, (int)Math.Round(physicalBounds.Bottom) - top);
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            window.Width = width / Scale;
            window.Height = height / Scale;
            const uint flags = 0x0004 | 0x0010 | 0x0200; // NOZORDER | NOACTIVATE | NOOWNERZORDER
            if (!SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // WPF may accept a suggested WM_DPICHANGED rectangle during the first move.
            NativeRect actual;
            if (GetWindowRect(hwnd, out actual) && (actual.Left != left || actual.Top != top ||
                actual.Right != left + width || actual.Bottom != top + height))
                SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, flags);
        }

        internal static bool TryPointer(out Point point)
        {
            NativePoint native;
            bool available = GetCursorPos(out native);
            point = new Point(native.X, native.Y);
            return available;
        }

        internal static bool InDockCorridor(Window window, Point cursor, ScreenInfo screen)
        {
            var h = new WindowInteropHelper(window).Handle;
            NativeRect r;
            return h != IntPtr.Zero && GetWindowRect(h, out r) && cursor.X >= r.Left && cursor.X < r.Right &&
                cursor.Y >= r.Top && cursor.Y < screen.Bounds.Bottom + 6;
        }

        internal static bool TryGetTop(Window window, out double top)
        {
            NativeRect bounds;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out bounds)) { top = bounds.Top; return true; }
            top = 0;
            return false;
        }

        internal static ScreenInfo[] Read()
        {
            var list = new List<ScreenInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref NativeRect frame, IntPtr ignored)
            {
                var info = new NativeMonitor { Size = Marshal.SizeOf(typeof(NativeMonitor)) };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                var target = new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
                // EDD_GET_DEVICE_INTERFACE_NAME returns the monitor interface path (including
                // the display instance), which survives ordinary layout/primary changes.
                bool found = EnumDisplayDevices(info.Device, 0, ref target, 1);
                string id = found && !String.IsNullOrWhiteSpace(target.Id) ? target.Id : info.Device;
                if (list.Exists(s => String.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))) id = info.Device;
                uint x, y;
                double scale = GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x != 0 ? x / 96.0 : 1;
                var bounds = info.Screen.ToRect();
                int prefix = info.Device.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
                string number = prefix >= 0 ? info.Device.Substring(prefix + 7) : (list.Count + 1).ToString();
                string label = Loc.Format("Display", number, bounds.Width, bounds.Height);
                list.Add(new ScreenInfo(id, label, (info.Flags & 1) != 0, bounds, info.Work.ToRect(), scale) { Handle = monitor });
                return true;
            }, IntPtr.Zero);
            if (list.Count == 0)
            {
                var bounds = new Rect(0, 0, Math.Max(1, GetSystemMetrics(0)), Math.Max(1, GetSystemMetrics(1)));
                list.Add(new ScreenInfo("fallback", Loc.Get("MainDisplay"), true, bounds, bounds, 1));
            }
            list.Sort((a,b) => a.Primary != b.Primary ? (a.Primary ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(a.Label,b.Label));
            return list.ToArray();
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            public Rect ToRect() { return new Rect(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top)); }
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct NativeMonitor
        {
            public int Size; public NativeRect Screen, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Device;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Label;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Key;
        }
        private delegate bool MonitorProc(IntPtr monitor, IntPtr dc, ref NativeRect area, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitor info);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool EnumDisplayDevices(string name, uint index, ref DisplayDevice device, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insert, int x, int y, int width, int height, uint flags);
    }
}
