using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Lunaris
{
    internal sealed class WindowList
    {
        private readonly ShellCatalog catalog;
        private readonly int owner = Process.GetCurrentProcess().Id;

        internal WindowList(ShellCatalog catalog) { this.catalog = catalog; }

        internal List<WindowEntry> Read()
        {
            var list = new List<WindowEntry>();
            EnumWindows(delegate(IntPtr window, IntPtr arg)
            {
                uint pid;
                GetWindowThreadProcessId(window, out pid);
                if (pid == owner || !IsWindowVisible(window) || ShellWindow(window)) return true;
                long style = GetWindowLongPtr(window, -20).ToInt64();
                if ((style & 0x08000080) != 0 || ((style & 0x40000) == 0 && GetWindow(window, 4) != IntPtr.Zero)) return true;
                int cloaked;
                if (DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                var title = new StringBuilder(Math.Min(32767, GetWindowTextLength(window)) + 1);
                if (GetWindowText(window, title, title.Capacity) == 0) return true;
                string path, processId;
                ReadProcess(pid, out path, out processId);
                string windowId = AppId(window);
                string className = Class(window);
                // The AppsFolder Explorer entry points to a shell namespace, not explorer.exe.
                if ((className == "CabinetWClass" || className == "ExploreWClass") &&
                    String.Equals(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "explorer.exe"), StringComparison.OrdinalIgnoreCase))
                    processId = "Microsoft.Windows.Explorer";
                // Older UWP applications place their CoreWindow in ApplicationFrameHost.
                if (className == "ApplicationFrameWindow")
                {
                    EnumChildWindows(window, delegate(IntPtr child, IntPtr unused)
                    {
                        uint childPid;
                        GetWindowThreadProcessId(child, out childPid);
                        if (childPid == pid || Class(child) != "Windows.UI.Core.CoreWindow") return true;
                        string childPath, childId;
                        ReadProcess(childPid, out childPath, out childId);
                        if (childPath != null) path = childPath;
                        if (childId != null) processId = childId;
                        return false;
                    }, IntPtr.Zero);
                }
                AppEntry app = catalog.Match(windowId, processId, path);
                if (app != null) list.Add(new WindowEntry { Handle = window, Title = title.ToString(), AppId = app.Id, App = app, ProcessPath = path });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        internal static void Focus(IntPtr window)
        {
            if (!IsWindow(window)) return;
            if (IsIconic(window)) ShowWindowAsync(window, 9);
            if (SetForegroundWindow(window) || IsHungAppWindow(window)) return;
            IntPtr front = GetForegroundWindow();
            if (front == IntPtr.Zero || IsHungAppWindow(front)) return;
            uint ignored;
            uint foreground = GetWindowThreadProcessId(front, out ignored);
            uint current = GetCurrentThreadId();
            bool attached = foreground != current && AttachThreadInput(current, foreground, true);
            try { BringWindowToTop(window); SetForegroundWindow(window); }
            finally { if (attached) AttachThreadInput(current, foreground, false); }
        }

        internal bool IsFullscreen()
        {
            return FullscreenMonitor(true) != IntPtr.Zero;
        }

        // SPI_SETWORKAREA can leave an already maximized window on a secondary
        // display at its previous height. Resize only frames still filling the
        // old work area. GetWindowRect includes invisible DPI-dependent borders.
        internal static void UpdateMaximizedBounds(IntPtr monitor, Rect previous, int newBottom)
        {
            EnumWindows(delegate(IntPtr hwnd, IntPtr ignored) {
                if (!IsZoomed(hwnd) || !IsWindowVisible(hwnd) || MonitorFromWindow(hwnd, 0) != monitor ||
                    (GetWindowLongPtr(hwnd, -16).ToInt64() & 0x00C40000) == 0) return true;
                Rect frame, raw;
                if (DwmGetWindowBounds(hwnd, 9, out frame, 16) != 0 || frame.Left != previous.Left || frame.Right != previous.Right ||
                    frame.Top != previous.Top || frame.Bottom != previous.Bottom || !GetWindowRect(hwnd, out raw)) return true;
                const uint flags = 0x0002 | 0x0004 | 0x0010 | 0x0200 | 0x4000; // NOMOVE, NOZORDER, NOACTIVATE, NOOWNERZORDER, ASYNCWINDOWPOS
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, raw.Right - raw.Left,
                    raw.Bottom - raw.Top + newBottom - previous.Bottom, flags);
                return true;
            }, IntPtr.Zero);
        }

        internal IntPtr FullscreenMonitor(bool primaryOnly)
        {
            IntPtr window = GetForegroundWindow();
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (window == IntPtr.Zero || pid == owner || ShellWindow(window) || IsIconic(window)) return IntPtr.Zero;
            // A maximized browser can paint even its title bar inside the client area.
            // Chromium removes caption and resize frame when entering actual F11 fullscreen.
            if (IsZoomed(window) && (GetWindowLongPtr(window, -16).ToInt64() & 0x00C40000) != 0) return IntPtr.Zero;
            Rect client;
            if (!GetClientRect(window, out client)) return IntPtr.Zero;
            Point origin = new Point();
            if (!ClientToScreen(window, ref origin)) return IntPtr.Zero;
            MonitorInfo monitor = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            IntPtr handle = MonitorFromWindow(window, 2);
            if (!GetMonitorInfo(handle, ref monitor) || (primaryOnly && (monitor.Flags & 1) == 0)) return IntPtr.Zero;
            return origin.X <= monitor.Bounds.Left && origin.Y <= monitor.Bounds.Top &&
                origin.X + client.Right >= monitor.Bounds.Right && origin.Y + client.Bottom >= monitor.Bounds.Bottom ? handle : IntPtr.Zero;
        }

        internal HashSet<string> CoveredScreens(ScreenInfo[] screens)
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<IntPtr>();
            // Walk explicit desktop Z-order. The foreground window alone describes only one
            // display, and changes to a Dock popup must not discard the underlying app.
            for (IntPtr hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero && visited.Add(hwnd) &&
                decided.Count < screens.Length; hwnd = GetWindow(hwnd, 2))
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == owner || !IsWindowVisible(hwnd) || IsIconic(hwnd) || ShellWindow(hwnd)) continue;
                long exStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
                if ((exStyle & 0x08000080) != 0 || ((exStyle & 0x40000) == 0 && GetWindow(hwnd, 4) != IntPtr.Zero)) continue;
                int cloaked;
                if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) continue;
                Rect outer;
                if (DwmGetWindowBounds(hwnd, 9, out outer, 16) != 0 && !GetWindowRect(hwnd, out outer)) continue;
                Rect client;
                Point origin = new Point();
                if (!GetClientRect(hwnd, out client) || !ClientToScreen(hwnd, ref origin)) continue;
                var content = new System.Windows.Rect(origin.X, origin.Y, Math.Max(0,client.Right), Math.Max(0,client.Bottom));
                var frame = new System.Windows.Rect(outer.Left, outer.Top, Math.Max(0,outer.Right-outer.Left),
                                                    Math.Max(0,outer.Bottom-outer.Top));
                bool maximized = IsZoomed(hwnd);
                IntPtr assigned = MonitorFromWindow(hwnd, 2);
                foreach (var screen in screens)
                {
                    if (decided.Contains(screen.Id)) continue;
                    var overlap = System.Windows.Rect.Intersect(screen.Bounds, frame);
                    double threshold = 24 * screen.Scale;
                    if (overlap.IsEmpty || overlap.Width < threshold || overlap.Height < threshold) continue;
                    // Maximized nonclient frames sometimes spill a few pixels into a neighbour.
                    // Only the substantial intersection above can make an ordinary window frontmost there.
                    decided.Add(screen.Id);
                    if (content.Contains(screen.Bounds) || (assigned == screen.Handle &&
                        (maximized || frame.Contains(screen.WorkArea)))) covered.Add(screen.Id);
                }
            }
            return covered;
        }

        internal static string Class(IntPtr window)
        {
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            return name.ToString();
        }

        private static bool ShellWindow(IntPtr window)
        {
            string name = Class(window);
            return name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" ||
                name == "Shell_SecondaryTrayWnd" || name == "DV2ControlHost";
        }

        private static void ReadProcess(uint pid, out string path, out string id)
        {
            path = null;
            id = null;
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return;
            try
            {
                int length = 32768;
                var file = new StringBuilder(length);
                if (QueryFullProcessImageName(process, 0, file, ref length)) path = file.ToString();
                uint size = 0;
                if (GetApplicationUserModelId(process, ref size, null) == 122 && size > 0 && size < 32768)
                {
                    var result = new StringBuilder((int)size);
                    if (GetApplicationUserModelId(process, ref size, result) == 0) id = result.ToString();
                }
            }
            finally { CloseHandle(process); }
        }

        private static string AppId(IntPtr window)
        {
            Guid iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            if (SHGetPropertyStoreForWindow(window, ref iid, out store) != 0 || store == null) return null;
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            var value = new Variant();
            try
            {
                if (store.GetValue(ref key, ref value) == 0 && value.Type == 31)
                    return Marshal.PtrToStringUni(value.Pointer);
                return null;
            }
            finally { PropVariantClear(ref value); Marshal.ReleaseComObject(store); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size=24)] private struct Variant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, ref Variant value);
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Bounds, Work; public int Flags; }
        internal delegate bool EnumProc(IntPtr window, IntPtr arg);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr arg);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumProc callback, IntPtr arg);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int cmd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint source, uint target, bool attach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr parent);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll", EntryPoint="DwmGetWindowAttribute")] private static extern int DwmGetWindowBounds(IntPtr hwnd, int attr, out Rect rect, int bytes);
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attr, out int value, int bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint rights, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint size, StringBuilder id);
        [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IPropertyStore store);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
    }
}
