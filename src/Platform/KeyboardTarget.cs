using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Lunaris
{
    // Remember the focused child as well as the foreground top-level window.
    // A hosted editor can belong to a different GUI thread from its frame.
    internal sealed class KeyboardTarget
    {
        private IntPtr window, focus;
        private uint windowThread, focusThread;
        private uint windowPid, focusPid;

        internal void Observe(IntPtr bar)
        {
            IntPtr active = GetForegroundWindow();
            if (active == bar || active == IntPtr.Zero) return;
            window = active;
            windowThread = WindowList.GetWindowThreadProcessId(window, out windowPid);
            var info = new ThreadInfo { Size = Marshal.SizeOf(typeof(ThreadInfo)) };
            focus = GetGUIThreadInfo(windowThread, ref info) && info.Focus != IntPtr.Zero ? info.Focus : window;
            focusThread = WindowList.GetWindowThreadProcessId(focus, out focusPid);
        }

        internal IntPtr Layout { get { return GetKeyboardLayout(focusThread); } }

        internal static string Label(IntPtr layout)
        {
            try { return CultureInfo.GetCultureInfo((int)(layout.ToInt64() & 0xffff)).TwoLetterISOLanguageName.ToUpperInvariant(); }
            catch (CultureNotFoundException) { return "⌨"; }
        }

        internal bool Select(IntPtr layout)
        {
            uint pid;
            if (window == IntPtr.Zero || WindowList.GetWindowThreadProcessId(window, out pid) != windowThread || pid != windowPid)
                return false;
            if (WindowList.GetWindowThreadProcessId(focus, out pid) != focusThread || pid != focusPid) return false;
            // Return activation first so Windows restores the editor's per-app
            // input profile before it handles WM_INPUTLANGCHANGEREQUEST.
            WindowList.Focus(window);
            if (GetForegroundWindow() != window) return false;
            return PostMessage(focus, 0x0050, IntPtr.Zero, layout);
        }

        internal static Forms.InputLanguageCollection Installed { get { return Forms.InputLanguage.InstalledInputLanguages; } }

        [StructLayout(LayoutKind.Sequential)] private struct ThreadInfo {
            public int Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int Left, Top, Right, Bottom;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref ThreadInfo info);
        [DllImport("user32.dll", EntryPoint="PostMessageW")] private static extern bool PostMessage(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
