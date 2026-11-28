using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Lunaris
{
    // Reserve physical pixels with Explorer. A preview positions the same window
    // without registering an appbar or changing the user's work area.
    internal sealed class StatusBarPosition : IDisposable
    {
        private readonly Window window;
        private readonly HwndSource source;
        private readonly bool preview;
        private readonly uint callback = RegisterWindowMessage("Lunaris.StatusBar.AppBar");
        private readonly uint shellCreated = RegisterWindowMessage("TaskbarCreated");
        private ScreenInfo screen;
        private Rect last = Rect.Empty;
        private DateTime retryAt = DateTime.MaxValue;
        private bool registered, pending, disposed;
        internal double Bottom { get { return last.IsEmpty ? screen.Bounds.Top : last.Bottom; } }

        internal StatusBarPosition(Window window, ScreenInfo screen, bool preview)
        {
            this.window = window; this.screen = screen; this.preview = preview;
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
            source.AddHook(OnMessage);
            Position();
        }

        internal void Update(ScreenInfo next)
        {
            bool changed = !next.SameLayout(screen);
            screen = next;
            if (changed || (!preview && (!registered || DateTime.UtcNow >= retryAt))) Position();
        }

        private void Position()
        {
            if (disposed) return;
            var data = new AppBarData { Size = Marshal.SizeOf(typeof(AppBarData)), Window = source.Handle,
                Callback = callback, Edge = 1 }; // ABE_TOP
            if (!preview && !registered) {
                registered = SHAppBarMessage(0, ref data) != UIntPtr.Zero;
                if (registered) last = Rect.Empty;
            }
            var bounds = registered ? screen.Bounds : screen.WorkArea;
            int height = Math.Max(1, (int)Math.Round(30 * screen.Scale));
            data.Bounds = new NativeRect { Left = (int)bounds.Left, Right = (int)bounds.Right,
                Top = (int)bounds.Top, Bottom = (int)bounds.Top + height };
            bool queried = !registered || PositionMessage(2, ref data); // ABM_QUERYPOS
            data.Bounds.Bottom = data.Bounds.Top + height;
            if (queried && retryAt == DateTime.MaxValue && last == data.Bounds.Rect) return;
            bool reserved = !registered || (queried && PositionMessage(3, ref data)); // ABM_SETPOS
            retryAt = reserved ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(1);
            last = data.Bounds.Rect;
            screen.Place(window, last);
            if (registered && reserved) SHAppBarMessage(9, ref data); // ABM_WINDOWPOSCHANGED
        }

        private static bool PositionMessage(uint operation, ref AppBarData data)
        {
            if (!NotificationArea.UsesTrayHost) return SHAppBarMessage(operation, ref data) != UIntPtr.Zero;
            var wire = new AppBarRelay.Data {
                Size = 40, Window = unchecked((uint)data.Window.ToInt64()), Callback = data.Callback, Edge = data.Edge,
                Left = data.Bounds.Left, Top = data.Bounds.Top, Right = data.Bounds.Right, Bottom = data.Bounds.Bottom
            };
            if (AppBarRelay.Position(TaskbarGuard.ShellTaskbar(), data.Window, operation, ref wire) == IntPtr.Zero) return false;
            data.Bounds = new NativeRect { Left = wire.Left, Top = wire.Top, Right = wire.Right, Bottom = wire.Bottom };
            return true;
        }

        private IntPtr OnMessage(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (preview || disposed) return IntPtr.Zero;
            if ((uint)msg == shellCreated) { registered = false; last = Rect.Empty; }
            else if ((uint)msg != callback || w.ToInt32() != 1) return IntPtr.Zero; // ABN_POSCHANGED
            if (!pending) {
                pending = true;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { pending = false; Position(); }));
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            disposed = true;
            source.RemoveHook(OnMessage);
            if (registered) {
                var data = new AppBarData { Size = Marshal.SizeOf(typeof(AppBarData)), Window = source.Handle };
                SHAppBarMessage(1, ref data); // ABM_REMOVE
                registered = false;
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect {
            public int Left, Top, Right, Bottom;
            public Rect Rect { get { return new Rect(Left, Top, Math.Max(1,Right-Left), Math.Max(1,Bottom-Top)); } }
        }
        [StructLayout(LayoutKind.Sequential)] private struct AppBarData {
            public int Size; public IntPtr Window; public uint Callback, Edge; public NativeRect Bounds; public IntPtr Parameter;
        }
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    }
}
