using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;
using Rect = Lunaris.WindowList.Rect;

namespace Lunaris
{
    internal sealed class TaskbarGuard : IDisposable
    {
        private Process guard;
        private EventWaitHandle stop;
        private static string LockName
        {
            get { return @"Local\Lunaris.Taskbar." + WindowsIdentity.GetCurrent().User.Value; }
        }
        private static string Journal
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LunarisDock", "Recovery", "taskbar-" + Process.GetCurrentProcess().SessionId + ".txt");
            }
        }
        internal bool Running { get { return guard != null && !guard.HasExited; } }

        internal void Start()
        {
            if (Running) return;
            Stop();
            using (var parent = Process.GetCurrentProcess())
            {
                string token = Guid.NewGuid().ToString("N");
                string prefix = @"Local\Lunaris.Guard." + token;
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Ready"))
                {
                    stop = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Stop");
                    try
                    {
                        guard = Process.Start(new ProcessStartInfo(parent.MainModule.FileName,
                            String.Format(CultureInfo.InvariantCulture, "--guardian {0} {1} {2}", parent.Id,
                            parent.StartTime.ToUniversalTime().Ticks, token)) { UseShellExecute = false, CreateNoWindow = true });
                        var limit = Stopwatch.StartNew();
                        while (!ready.WaitOne(80))
                        {
                            if (guard.HasExited || limit.ElapsedMilliseconds > 12000)
                                throw new InvalidOperationException(Loc.Get("ReplaceTaskbarError"));
                        }
                    }
                    catch { Stop(); throw; }
                }
            }
        }

        internal void Stop()
        {
            if (stop != null) stop.Set();
            try
            {
                if (guard != null && !guard.WaitForExit(12000))
                    throw new InvalidOperationException(Loc.Get("TaskbarRecovering"));
                if (guard != null && File.Exists(Journal)) Recover();
            }
            finally
            {
                if (guard != null) { guard.Dispose(); guard = null; }
                if (stop != null) { stop.Dispose(); stop = null; }
            }
        }
        public void Dispose() { Stop(); }

        internal static void Recover()
        {
            if (!File.Exists(Journal)) return;
            using (var gate = new Mutex(false, LockName))
            {
                if (!Acquire(gate, 5500)) return; // A live guardian owns this session.
                try { if (!Restore()) throw new IOException(Loc.Get("ExplorerRequired")); }
                finally { gate.ReleaseMutex(); }
            }
        }

        internal static int Run(string[] args)
        {
            int parentId;
            long ticks;
            Guid token;
            if (args.Length != 4 || !Int32.TryParse(args[1], out parentId) || !Int64.TryParse(args[2], out ticks) ||
                !Guid.TryParseExact(args[3], "N", out token)) return 2;
            string prefix = @"Local\Lunaris.Guard." + token.ToString("N");
            try
            {
                using (var parent = Process.GetProcessById(parentId))
                using (var self = Process.GetCurrentProcess())
                using (var ready = EventWaitHandle.OpenExisting(prefix + ".Ready"))
                using (var end = EventWaitHandle.OpenExisting(prefix + ".Stop"))
                using (var gate = new Mutex(false, LockName))
                {
                    if (parent.SessionId != self.SessionId || parent.StartTime.ToUniversalTime().Ticks != ticks ||
                        !String.Equals(parent.MainModule.FileName, self.MainModule.FileName, StringComparison.OrdinalIgnoreCase)) return 2;
                    if (!Acquire(gate, 5000)) return 3;
                    try
                    {
                        if (!Restore() || end.WaitOne(0) || parent.HasExited) return 4;
                        IntPtr tray = ShellTaskbar();
                        if (tray == IntPtr.Zero) return 4;
                        var journal = new List<string> { GetAutoHide() ? "1" : "0" };
                        foreach (IntPtr window in Bars())
                            journal.Add((WindowList.IsWindowVisible(window) ? "+" : "-") + Device(window));
                        WriteSnapshot(journal.ToArray());
                        try
                        {
                            return Supervise(parent, end, ready, journal);
                        }
                        finally
                        {
                            // Explorer can restart well after the owner died. The guardian stays in this
                            // logon session until the new shell acknowledges restoration or logoff ends it.
                            while (!Restore()) Thread.Sleep(250);
                        }
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { if (!ExpectedFailure(ex)) throw; return 5; }
        }

        private static bool ExpectedFailure(Exception ex)
        {
            return ex is WaitHandleCannotBeOpenedException || ex is ArgumentException || ex is IOException ||
                ex is UnauthorizedAccessException || ex is Win32Exception || ex is InvalidOperationException;
        }

        private static bool Acquire(Mutex gate, int timeout)
        {
            try { return gate.WaitOne(timeout); }
            catch (AbandonedMutexException) { return true; }
        }

        private static int Supervise(Process parent, EventWaitHandle end, EventWaitHandle ready, List<string> journal)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            var stateCheck = Stopwatch.StartNew();
            IntPtr lastTray = IntPtr.Zero;
            Func<bool> refresh = delegate
            {
                IntPtr tray = ShellTaskbar();
                if (tray == IntPtr.Zero) { lastTray = IntPtr.Zero; return false; }
                if (tray != lastTray || stateCheck.ElapsedMilliseconds >= 1500)
                {
                    if (GetAutoHide()) SetAutoHide(false);
                    lastTray = tray;
                    stateCheck.Restart();
                }
                var bars = Bars();
                bool readyForWindows = bars.Contains(tray);
                foreach (IntPtr bar in bars)
                {
                    Hide(bar);
                    if (WindowList.IsWindowVisible(bar) || !UpdateWorkArea(bar, journal, false)) readyForWindows = false;
                }
                return readyForWindows;
            };
            WinEvent callback = delegate(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint time)
            {
                if (hwnd == IntPtr.Zero || obj != 0 || child != 0) return;
                string name = WindowList.Class(hwnd);
                if ((name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") && IsShellWindow(hwnd)) Hide(hwnd);
            };
            IntPtr observer = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, callback, 0, 0, 0);
            try
            {
                // ShowWindowAsync posts to Explorer. A queued request is not proof that the
                // taskbar is hidden. A bounded acknowledgement keeps the settings UI honest.
                var deadline = Stopwatch.StartNew();
                for (;;)
                {
                    if (parent.HasExited || end.WaitOne(0)) return 4;
                    if (refresh() && !GetAutoHide()) break;
                    if (deadline.ElapsedMilliseconds >= 3200) return 4;
                    Thread.Sleep(40);
                }
                ready.Set();
                timer.Tick += delegate
                {
                    if (parent.HasExited || end.WaitOne(0)) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    else refresh();
                };
                timer.Start();
                Dispatcher.Run();
            }
            finally
            {
                timer.Stop();
                if (observer != IntPtr.Zero) UnhookWinEvent(observer);
                GC.KeepAlive(callback);
            }
            return 0;
        }

        private static void Hide(IntPtr window)
        {
            if (WindowList.IsWindowVisible(window)) ShowWindowAsync(window, 0);
        }

        internal static bool IsShellWindow(IntPtr window)
        {
            uint shell, owner;
            return GetWindowThreadProcessId(GetShellWindow(), out shell) != 0 &&
                GetWindowThreadProcessId(window, out owner) != 0 && shell == owner;
        }

        internal static IntPtr ShellTaskbar()
        {
            IntPtr window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, "Shell_TrayWnd", null)) != IntPtr.Zero)
                if (IsShellWindow(window)) return window;
            return IntPtr.Zero;
        }

        private static List<IntPtr> Bars()
        {
            var bars = new List<IntPtr>();
            IntPtr primary = ShellTaskbar();
            if (primary != IntPtr.Zero) bars.Add(primary);
            IntPtr last = IntPtr.Zero;
            while ((last = FindWindowEx(IntPtr.Zero, last, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                if (IsShellWindow(last)) bars.Add(last);
            return bars;
        }

        private static void WriteSnapshot(string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Journal));
            string temporary = Journal + ".new";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(file))
            {
                foreach (string line in lines) writer.WriteLine(line);
                writer.Flush();
                file.Flush(true);
            }
            if (File.Exists(Journal)) File.Replace(temporary, Journal, null);
            else File.Move(temporary, Journal);
        }

        private static bool Restore()
        {
            if (!File.Exists(Journal)) return true;
            string[] lines = File.ReadAllLines(Journal);
            if (lines.Length == 0 || (lines[0] != "1" && lines[0] != "0"))
                throw new IOException(Loc.Get("RecoveryCorrupt"));
            bool autoHide = lines[0] == "1";
            var journal = new List<string>(lines);
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 6800)
            {
                if (ShellTaskbar() != IntPtr.Zero)
                {
                    List<IntPtr> bars = Bars();
                    foreach (IntPtr window in bars)
                        ShowWindowAsync(window, Array.IndexOf(lines, "-" + Device(window)) >= 0 ? 0 : 8);
                    if (GetAutoHide() != autoHide) SetAutoHide(autoHide);
                    if (bars.Count != 0 && GetAutoHide() == autoHide)
                    {
                        bool visible = true;
                        foreach (IntPtr window in bars)
                            if (WindowList.IsWindowVisible(window) == (Array.IndexOf(lines, "-" + Device(window)) >= 0)) visible = false;
                        if (visible && (autoHide || bars.TrueForAll(window => UpdateWorkArea(window, journal, true))))
                        {
                            File.Delete(Journal); return true;
                        }
                    }
                }
                Thread.Sleep(90);
            }
            return false;
        }

        private static string Device(IntPtr hwnd)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info);
            return WindowList.Class(hwnd) + ":" + info.Device;
        }

        // A hidden Windows 11 taskbar can still own the bottom of rcWork. Change
        // only an edge that coincides with its real window. Keep every other
        // reservation, including the Lunaris status bar, as reported by Windows.
        private static bool UpdateWorkArea(IntPtr bar, List<string> journal, bool restoring)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            Rect taskbar;
            IntPtr monitor = MonitorFromWindow(bar, 2);
            if (!GetMonitorInfo(monitor, ref info) || !WindowList.GetWindowRect(bar, out taskbar)) return false;
            var bounds = info.Bounds;
            var work = info.Work;
            if (taskbar.Bottom != bounds.Bottom || taskbar.Top >= taskbar.Bottom || taskbar.Top <= bounds.Top ||
                taskbar.Left > work.Left || taskbar.Right < work.Right) return true;
            if (work.Bottom != (restoring ? bounds.Bottom : taskbar.Top)) return true;

            string marker = "work:" + WindowList.Class(bar) + ":" + info.Device;
            if (!journal.Contains(marker))
            {
                if (restoring) return true;
                journal.Add(marker);
                WriteSnapshot(journal.ToArray());
            }
            work.Bottom = restoring ? taskbar.Top : bounds.Bottom;
            // Session only. Broadcasting to Explorer makes it negotiate the
            // hidden taskbar's space again.
            if (!SystemParametersInfo(0x002F, 0, ref work, 0)) return false; // SPI_SETWORKAREA
            WindowList.UpdateMaximizedBounds(monitor, info.Work, work.Bottom);
            return true;
        }

        private static bool GetAutoHide()
        {
            var data = new AppBar { Size = Marshal.SizeOf(typeof(AppBar)) };
            return (SHAppBarMessage(4, ref data).ToUInt64() & 1) != 0;
        }

        private static void SetAutoHide(bool enabled)
        {
            var data = new AppBar { Size = Marshal.SizeOf(typeof(AppBar)), Hwnd = ShellTaskbar() };
            if (data.Hwnd == IntPtr.Zero) return;
            long state = unchecked((long)SHAppBarMessage(4, ref data).ToUInt64());
            data.Parameter = new IntPtr(enabled ? state | 1L : state & ~1L);
            SHAppBarMessage(10, ref data);
        }

        private delegate void WinEvent(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)] private struct AppBar
        {
            public int Size;
            public IntPtr Hwnd;
            public uint Callback, Edge;
            public Rect Bounds;
            public IntPtr Parameter;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct MonitorInfo
        {
            public int Size;
            public Rect Bounds, Work;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Device;
        }
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint parameter, ref Rect value, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr last, string name, string caption);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int cmd);
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref AppBar data);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr observer);
    }
}
