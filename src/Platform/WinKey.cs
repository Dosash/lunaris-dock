using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Lunaris
{
    // Only the two modifier bits and whether a chord occurred survive a callback.
    internal sealed class WinGesture
    {
        private int held;
        private bool chord;
        internal void Synchronize(bool leftDown, bool rightDown)
        {
            // A Win-up can be delivered on the secure desktop after Win+L.
            held &= (leftDown ? 1 : 0) | (rightDown ? 2 : 0);
            if (held == 0) chord = false;
        }
        internal bool Input(int key, bool down, bool otherModifier)
        {
            int bit = key == 0x5B ? 1 : key == 0x5C ? 2 : 0;
            if (bit == 0)
            {
                if (held != 0) chord = true;
                return false;
            }
            if (down)
            {
                if ((held & bit) == 0)
                {
                    chord = held != 0 || otherModifier;
                    held |= bit;
                }
                return false;
            }
            if ((held & bit) == 0) return false;
            held &= ~bit;
            return held == 0 && !chord;
        }
    }

    internal sealed class WinKey : IDisposable
    {
        private readonly Action toggle;
        private readonly WinGesture gesture = new WinGesture();
        private readonly HookProc callback;
        private readonly ManualResetEvent started = new ManualResetEvent(false);
        private Dispatcher dispatcher;
        private Thread thread;
        private Exception startError;
        private static readonly UIntPtr Marker = new UIntPtr(0x4C6E7244u);
        internal WinKey(Action toggle)
        {
            this.toggle = toggle;
            callback = Hook;
        }

        internal void Start()
        {
            thread = new Thread(Pump) { IsBackground = true, Name = "Lunaris Win key" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            started.WaitOne();
            if (startError != null) throw new InvalidOperationException(Loc.Get("WinKeyError"), startError);
        }

        private void Pump()
        {
            IntPtr hook = IntPtr.Zero;
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { startError = ex; }
            finally { started.Set(); }
            if (hook == IntPtr.Zero) return;
            try { Dispatcher.Run(); }
            finally { UnhookWindowsHookEx(hook); }
        }

        private IntPtr Hook(int code, IntPtr message, IntPtr address)
        {
            if (code >= 0)
            {
                var key = (KeyboardEvent)Marshal.PtrToStructure(address, typeof(KeyboardEvent));
                bool down = message.ToInt32() == 0x100 || message.ToInt32() == 0x104;
                bool otherHeld = false;
                if (key.Extra != Marker && (key.Key == 0x5B || key.Key == 0x5C) && down)
                {
                    // WH_KEYBOARD_LL runs before the asynchronous state of this event changes.
                    gesture.Synchronize(GetAsyncKeyState(0x5B) < 0, GetAsyncKeyState(0x5C) < 0);
                    for (int vk = 8; vk <= 0xFE && !otherHeld; ++vk)
                        if (vk != 0x5B && vk != 0x5C) otherHeld = GetAsyncKeyState(vk) < 0;
                }
                if (key.Extra != Marker && gesture.Input((int)key.Key, down, otherHeld))
                {
                    // Physical Win-down already reached Explorer, so every real chord is native.
                    // Mask its standalone menu gesture before delivering the matching Win-up.
                    var inputs = new [] { MakeKey(0xE8, 0, 0), MakeKey(0xE8, 0, 2),
                        MakeKey((ushort)key.Key, (ushort)key.Scan, 3) };
                    if (SendInput(3, inputs, Marshal.SizeOf(typeof(Input))) == 3)
                    {
                        toggle();
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, address);
        }

        private static Input MakeKey(ushort key, ushort scan, uint flags)
        {
            return new Input { Type = 1, Key = key, Scan = scan, Flags = flags, Extra = Marker };
        }

        public void Dispose()
        {
            if (dispatcher != null && thread != null && thread.IsAlive)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                thread.Join();
            }
            started.Dispose();
        }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardEvent
        {
            public uint Key, Scan, Flags, Time;
            public UIntPtr Extra;
        }
        // x64 INPUT union is 32 bytes at offset 8. KEYBDINPUT uses the first 24 bytes.
        [StructLayout(LayoutKind.Explicit, Size=40)] private struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ushort Key;
            [FieldOffset(10)] public ushort Scan;
            [FieldOffset(12)] public uint Flags;
            [FieldOffset(24)] public UIntPtr Extra;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    }
}
