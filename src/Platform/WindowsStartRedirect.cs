using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Lunaris
{
    internal sealed class WindowsStartRedirect : IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly Action showDockMenu;
        private readonly WinEvent callback;
        private IntPtr hook;
        private static readonly string StartHost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe");

        internal WindowsStartRedirect(Dispatcher dispatcher, Action showDockMenu)
        {
            this.dispatcher = dispatcher;
            this.showDockMenu = showDockMenu;
            callback = OnForeground;
        }

        internal void Enable()
        {
            if (hook != IntPtr.Zero) return;
            const uint foreground = 3, skipOwnProcess = 2;
            hook = SetWinEventHook(foreground, foreground, IntPtr.Zero, callback, 0, 0, skipOwnProcess);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private void OnForeground(IntPtr sender, uint eventId, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
        {
            if (sender != hook || hwnd == IntPtr.Zero || !IsWindowsStart(hwnd)) return;
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate {
                // If the user has already focused another app, leave their focus there.
                if (hook == sender && GetForegroundWindow() == hwnd) showDockMenu();
            }));
        }

        internal static bool IsWindowsStart(IntPtr hwnd)
        {
            uint pid;
            return GetWindowThreadProcessId(hwnd, out pid) != 0 && IsWindowsStartProcess(pid);
        }

        internal static bool IsWindowsStartProcess(uint pid)
        {
            var process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return false;
            try {
                int length = StartHost.Length + 8;
                var path = new StringBuilder(length);
                return QueryFullProcessImageName(process, 0, path, ref length) &&
                    String.Equals(path.ToString(), StartHost, StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseHandle(process); }
        }

        public void Dispose()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }

        private delegate void WinEvent(IntPtr hook, uint eventId, IntPtr hwnd, int obj, int child, uint thread, uint tick);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEvent handler, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
