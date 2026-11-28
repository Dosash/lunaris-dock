using System;
using System.Runtime.InteropServices;

namespace Lunaris
{
    // The shell sends an architecture-independent 40-byte APPBARDATA in a
    // 64-byte envelope. QUERY/SETPOS and GETTASKBARPOS write back through a
    // shell shared-memory token issued for a specific recipient process.
    internal static class AppBarRelay
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Data
        {
            internal int Size;
            internal uint Window, Callback, Edge;
            internal int Left, Top, Right, Bottom;
            internal long Parameter;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Request
        {
            internal Data Data;
            internal uint Operation, Padding;
            internal long Section;
            internal uint Recipient, Reserved;
        }

        internal static bool OutputRequest(IntPtr buffer, int bytes)
        {
            if (buffer == IntPtr.Zero || bytes != 64 || Marshal.ReadInt32(buffer) != 40) return false;
            int op = Marshal.ReadInt32(buffer, 40);
            return op == 2 || op == 3 || op == 5;
        }

        internal static IntPtr Forward(IntPtr explorer, IntPtr sender, IntPtr buffer, uint ourPid)
        {
            var request = (Request)Marshal.PtrToStructure(buffer, typeof(Request));
            // Shell32 on Windows 11 uses a global atom for an inter-process
            // section. Accept only tokens allocated for this receiver. The dock
            // routes its own requests directly, bypassing shell32's same-process
            // shortcut; a claimed heap address from an IPC packet is never used.
            if (request.Recipient != ourPid || request.Section < 0xC000 || request.Section > 0xFFFF)
                return IntPtr.Zero;
            IntPtr input = SHLockShared(new IntPtr(request.Section), ourPid);
            if (input == IntPtr.Zero) return IntPtr.Zero;
            try {
                Page page;
                if (VirtualQuery(input, out page, new UIntPtr((uint)Marshal.SizeOf(typeof(Page)))) == UIntPtr.Zero ||
                    page.Type != 0x40000 || page.State != 0x1000 || page.Protection != 4 ||
                    (ulong)(input.ToInt64() - page.Base.ToInt64()) + 40 > page.Length.ToUInt64() ||
                    Marshal.ReadInt32(input) != 40 || Marshal.ReadInt32(input, 4) != (int)request.Data.Window)
                    return IntPtr.Zero;
                var result = Position(explorer, sender, request.Operation, ref request.Data);
                if (result != IntPtr.Zero) {
                    if (request.Operation == 5) Marshal.WriteInt32(input, 12, (int)request.Data.Edge);
                    Marshal.WriteInt32(input, 16, request.Data.Left);
                    Marshal.WriteInt32(input, 20, request.Data.Top);
                    Marshal.WriteInt32(input, 24, request.Data.Right);
                    Marshal.WriteInt32(input, 28, request.Data.Bottom);
                }
                return result;
            } finally { SHUnlockShared(input); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Page
        {
            internal IntPtr Base, Allocation;
            internal uint InitialProtection;
            internal UIntPtr Length;
            internal uint State, Protection, Type;
        }
        [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr address, out Page page, UIntPtr size);
        internal static IntPtr Position(IntPtr explorer, IntPtr sender, uint operation, ref Data data)
        {
            uint target;
            if (GetWindowThreadProcessId(explorer, out target) == 0) return IntPtr.Zero;
            IntPtr shared = SHAllocShared(ref data, 40, target);
            if (shared == IntPtr.Zero) return IntPtr.Zero;
            try {
                var request = new Request { Data = data, Operation = operation, Section = shared.ToInt64(), Recipient = target };
                IntPtr payload = Marshal.AllocHGlobal(64);
                try {
                    Marshal.StructureToPtr(request, payload, false);
                    var packet = new TrayHost.CopyData { Length = 64, Buffer = payload };
                    IntPtr result = TrayHost.Forward(explorer, sender, ref packet);
                    if (result == IntPtr.Zero) return result;
                    IntPtr output = SHLockShared(shared, target);
                    if (output == IntPtr.Zero) return IntPtr.Zero;
                    try { data = (Data)Marshal.PtrToStructure(output, typeof(Data)); }
                    finally { SHUnlockShared(output); }
                    return result;
                } finally { Marshal.FreeHGlobal(payload); }
            } finally { SHFreeShared(shared, target); }
        }

        [DllImport("shlwapi.dll", EntryPoint="#7")] private static extern IntPtr SHAllocShared(ref Data data, uint size, uint target);
        [DllImport("shlwapi.dll", EntryPoint="#8")] private static extern IntPtr SHLockShared(IntPtr shared, uint target);
        [DllImport("shlwapi.dll", EntryPoint="#9")] private static extern bool SHUnlockShared(IntPtr data);
        [DllImport("shlwapi.dll", EntryPoint="#10")] private static extern bool SHFreeShared(IntPtr shared, uint target);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    }
}
