using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Lunaris
{
    // Shell_NotifyIcon locates a top-level Shell_TrayWnd. While Lunaris replaces
    // the taskbar, or a companion strip is expanded, relay icon registrations
    // to the desktop shell. Explorer continues to own its copy and its balloons.
    // Never move, show, activate or re-parent any Explorer window.
    internal sealed class TrayHost
    {
        private const string ClassName = "Shell_TrayWnd";
        private const uint CopyDataMessage = 0x4A;
        private readonly IntPtr instance = GetModuleHandle(null);
        private readonly WindowProcedure procedure;
        private readonly Dispatcher dispatcher;
        private readonly uint ownPid;
        private readonly HashSet<uint> resync = new HashSet<uint>();
        private IntPtr window;
        private IntPtr lastExplorer;
        private bool registered;
        internal volatile bool Unavailable;
        internal volatile bool Selected;
        private DispatcherTimer timer;
        private Mutex ownership;

        internal TrayHost()
        {
            procedure = OnMessage;
            using (var self = Process.GetCurrentProcess()) ownPid = (uint)self.Id;
            Dispatcher worker = null;
            using (var ready = new ManualResetEvent(false)) {
                var thread = new Thread(delegate() {
                    worker = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                }) { IsBackground = true, Name = "Lunaris notification area" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.WaitOne();
            }
            dispatcher = worker;
        }

        internal void SetOpen(bool open)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate {
                if (!open) { Close(); return; }
                if (timer != null) return;
                Tick();
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(window == IntPtr.Zero ? 1500 : 200) };
                timer.Tick += delegate {
                    Tick();
                    timer.Interval = TimeSpan.FromMilliseconds(window == IntPtr.Zero ? 1500 : 200);
                };
                timer.Start();
            }));
        }

        private void Tick()
        {
            if (window == IntPtr.Zero) { Open(); return; }
            var current = TaskbarGuard.ShellTaskbar();
            if (current != IntPtr.Zero) {
                if (FindWindow(ClassName, null) != window) Promote();
                if (current != lastExplorer) RequestExistingIcons(null);
            }
            lastExplorer = current;
        }

        private void Open()
        {
            if (window != IntPtr.Zero) return;
            Unavailable = true;
            IntPtr explorer = TaskbarGuard.ShellTaskbar();
            if (explorer == IntPtr.Zero || FindWindow(ClassName, null) != explorer) return;
            ownership = new Mutex(false, @"Local\Lunaris.TrayHost");
            bool acquired;
            try { acquired = ownership.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { ownership.Dispose(); ownership = null; return; }

            var cls = new WindowClass { Name = ClassName, Instance = instance, Procedure = procedure };
            if (!registered) registered = RegisterClass(ref cls) != 0;
            if (!registered) { ReleaseOwnership(); return; }
            // WS_POPUP; WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST. Never WS_VISIBLE.
            window = CreateWindowEx(0x08000088, ClassName, "Lunaris notification area",
                0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) {
                ReleaseOwnership();
                return;
            }
            lastExplorer = explorer;
            Promote();
            if (FindWindow(ClassName, null) != window) {
                DestroyWindow(window); window = IntPtr.Zero;
                ReleaseOwnership();
                return;
            }
            Selected = true;
            Unavailable = false;
            RequestExistingIcons(null);
        }

        private void Promote()
        {
            // Z order selects the recipient. No show/activation flags are used.
            SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0213);
        }

        private void RequestExistingIcons(HashSet<uint> processes)
        {
            uint created = RegisterWindowMessage("TaskbarCreated");
            uint shellPid;
            GetWindowThreadProcessId(TaskbarGuard.ShellTaskbar(), out shellPid);
            if (created == 0 || shellPid == 0) return;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
                uint pid;
                if (GetWindowThreadProcessId(hwnd, out pid) != 0 && pid != ownPid && pid != shellPid &&
                    (processes == null || processes.Contains(pid)))
                    PostMessage(hwnd, created, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        private void Close()
        {
            if (timer != null) { timer.Stop(); timer = null; }
            if (window != IntPtr.Zero) {
                DestroyWindow(window);
                window = IntPtr.Zero;
            }
            NotificationArea.Reset();
            Selected = false;
            // If Explorer was unavailable for an update, let just those owners
            // republish to its HWND now that the Lunaris HWND has gone.
            if (resync.Count > 0) { RequestExistingIcons(resync); resync.Clear(); }
            Unavailable = false;
            ReleaseOwnership();
        }

        private void ReleaseOwnership()
        {
            if (ownership == null) return;
            ownership.ReleaseMutex();
            ownership.Dispose();
            ownership = null;
        }

        private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l)
        {
            if (message == CopyDataMessage && l != IntPtr.Zero) {
                var packet = (CopyData)Marshal.PtrToStructure(l, typeof(CopyData));
                return HandlePacket(w, packet);
            }
            if (message == 0x0010) return IntPtr.Zero; // Owner releases the host on collapse.
            return DefWindowProc(hwnd, message, w, l);
        }

        private IntPtr HandlePacket(IntPtr sender, CopyData packet)
        {
            int command = -1;
            uint ownerPid = 0;
            IntPtr point;
            if (packet.Kind == new IntPtr(3) && NotificationArea.GetAnchor(packet.Buffer, packet.Length, out point))
                return point;
            if (packet.Kind != IntPtr.Zero && packet.Kind != new IntPtr(1) && packet.Kind != new IntPtr(3))
                return IntPtr.Zero;
            IntPtr explorer = TaskbarGuard.ShellTaskbar();
            if (packet.Kind == IntPtr.Zero && AppBarRelay.OutputRequest(packet.Buffer, packet.Length))
                return AppBarRelay.Forward(explorer, sender, packet.Buffer, ownPid);
            bool accepted = packet.Kind == new IntPtr(1) &&
                NotificationArea.Receive(packet.Buffer, packet.Length, out command, out ownerPid);
            if (accepted && command == 3) return new IntPtr(1);
            IntPtr response = Forward(explorer, sender, ref packet);
            if (!accepted || response != IntPtr.Zero) return response;
            if (command == 0 && explorer != IntPtr.Zero) {
                // Existing apps are reannouncing after TaskbarCreated. Explorer
                // may reject a duplicate ADD. Update its existing registration
                // with the same fields so the app can continue with SETVERSION.
                var bytes = new byte[packet.Length];
                Marshal.Copy(packet.Buffer, bytes, 0, bytes.Length);
                Buffer.BlockCopy(BitConverter.GetBytes(1), 0, bytes, 4, 4);
                var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try {
                    packet.Buffer = pin.AddrOfPinnedObject();
                    if (Forward(explorer, sender, ref packet) != IntPtr.Zero) return new IntPtr(1);
                } finally { pin.Free(); }
            }
            if (ownerPid != 0) resync.Add(ownerPid);
            return new IntPtr(1);
        }

        internal static IntPtr Forward(IntPtr explorer, IntPtr sender, ref CopyData packet)
        {
            IntPtr result;
            return explorer != IntPtr.Zero &&
                SendMessageTimeout(explorer, CopyDataMessage, sender, ref packet, 2, 1800, out result) != IntPtr.Zero ?
                result : IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CopyData { public IntPtr Kind; public int Length; public IntPtr Buffer; }
        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr w, IntPtr l);
        private delegate bool EnumWindow(IntPtr window, IntPtr data);
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Style;
            public WindowProcedure Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string Menu, Name;
        }

        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass cls);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls,
            string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr args);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd,
            uint message, IntPtr w, ref CopyData packet, uint flags, uint timeout, out IntPtr result);
    }
}
