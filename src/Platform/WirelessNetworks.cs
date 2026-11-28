using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;

namespace Lunaris
{
    internal sealed class WifiNetwork
    {
        internal Guid Adapter;
        internal string AdapterName, Name, Hex, Profile, Authentication, Encryption;
        internal int Signal;
        internal bool Connected, Connectable;
        internal bool HasProfile { get { return !String.IsNullOrEmpty(Profile); } }
        internal bool NeedsKey { get { return !HasProfile && Authentication != "open" && Authentication != "OWE"; } }
        internal bool Supported { get { return Connectable && (HasProfile || (Authentication != null && Encryption != null)); } }
    }

    internal sealed class WirelessNetworks
    {
        internal bool Ethernet, Wifi;
        internal int Adapters, Error;
        internal readonly List<WifiNetwork> Networks = new List<WifiNetwork>();

        internal static WirelessNetworks Read(bool refresh)
        {
            var result = new WirelessNetworks();
            try {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    switch (nic.NetworkInterfaceType) {
                        case NetworkInterfaceType.Ethernet:
                        case NetworkInterfaceType.Ethernet3Megabit:
                        case NetworkInterfaceType.FastEthernetFx:
                        case NetworkInterfaceType.FastEthernetT:
                        case NetworkInterfaceType.GigabitEthernet: result.Ethernet = true; break;
                        case NetworkInterfaceType.Wireless80211: result.Wifi = true; break;
                    }
                }
            } catch (NetworkInformationException) { }
            try {
                using (var wlan = new Client()) {
                    IntPtr ptr;
                    Check(WlanEnumInterfaces(wlan.Handle, IntPtr.Zero, out ptr));
                    Interface[] adapters;
                    try { adapters = ReadList<Interface>(ptr).ToArray(); }
                    finally { WlanFreeMemory(ptr); }
                    result.Adapters = adapters.Length;
                    if (refresh) {
                        // Logo-compliant WLAN drivers finish scanning within four seconds.
                        bool scanning = false;
                        foreach (var adapter in adapters) {
                            Guid id = adapter.Id;
                            uint code = WlanScan(wlan.Handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            if (code == 0) scanning = true; else result.Error = (int)code;
                        }
                        if (scanning) Thread.Sleep(4000);
                    }
                    foreach (var adapter in adapters) {
                        try { result.Networks.AddRange(wlan.List(adapter.Id, adapter.Name)); }
                        catch (Win32Exception ex) { result.Error = ex.NativeErrorCode; }
                    }
                }
            } catch (Win32Exception ex) { result.Error = ex.NativeErrorCode; }
            return result;
        }

        internal enum Command { NoChange, Connect, Disconnect }
        internal sealed class Request
        {
            internal Command Kind;
            internal Guid Adapter;
            internal string SsidHex;
            internal Connection Parameters;
        }

        internal static Request BuildRequest(WifiNetwork selected, List<WifiNetwork> current, bool disconnect, string key)
        {
            // Disconnect acts on an entire interface. Recheck the selected
            // connection, since it may have changed while its details were open.
            var entry = current.Find(n => n.Adapter == selected.Adapter && n.Hex == selected.Hex && n.Profile == selected.Profile &&
                n.Authentication == selected.Authentication && n.Encryption == selected.Encryption);
            if (entry == null || (disconnect && !entry.Connected)) throw new Win32Exception(1168);
            var request = new Request { Adapter = entry.Adapter, SsidHex = entry.Hex };
            if (disconnect) request.Kind = Command.Disconnect;
            else if (!entry.Connected) {
                if (!entry.Supported) throw new Win32Exception(50);
                request.Kind = Command.Connect;
                request.Parameters = new Connection { Mode = entry.HasProfile ? 0u : 1u, BssType = 1,
                    Profile = entry.HasProfile ? entry.Profile : TemporaryProfile(entry, key) };
            }
            return request;
        }

        internal static void Change(WifiNetwork network, bool disconnect, string key)
        {
            using (var wlan = new Client()) {
                var request = BuildRequest(network, wlan.List(network.Adapter, network.AdapterName), disconnect, key);
                if (request.Kind == Command.NoChange) return;
                if (request.Kind == Command.Disconnect) Check(WlanDisconnect(wlan.Handle, ref request.Adapter, IntPtr.Zero));
                else Check(WlanConnect(wlan.Handle, ref request.Adapter, ref request.Parameters, IntPtr.Zero));
                // WlanConnect/WlanDisconnect return when the request is queued.
                // Only report success once the selected SSID actually changes state.
                var wait = Stopwatch.StartNew();
                do {
                    Thread.Sleep(400);
                    bool connected = wlan.List(request.Adapter, network.AdapterName).Exists(n => n.Connected && n.Hex == request.SsidHex);
                    if (connected != disconnect) return;
                } while (wait.ElapsedMilliseconds < 20000);
                throw new Win32Exception(1460);
            }
        }

        private static string TemporaryProfile(WifiNetwork network, string key)
        {
            string shared = "";
            if (network.NeedsKey) {
                bool hexKey = key != null && key.Length == 64 && key.All(Uri.IsHexDigit) && network.Authentication != "WPA3SAE";
                if (!hexKey && (String.IsNullOrEmpty(key) || key.Length < 8 || key.Length > 63 || key.Any(Char.IsControl)))
                    throw new Win32Exception(86);
                shared = "<sharedKey><keyType>" + (hexKey ? "networkKey" : "passPhrase") +
                    "</keyType><protected>false</protected><keyMaterial>" + SecurityElement.Escape(key) + "</keyMaterial></sharedKey>";
            }
            // Hex SSID preserves arbitrary octets, including names which aren't UTF-8.
            // A temporary native profile keeps entered credentials out of Lunaris settings and files.
            return "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>" + network.Hex +
                "</name><SSIDConfig><SSID><hex>" + network.Hex + "</hex></SSID></SSIDConfig><connectionType>ESS</connectionType>" +
                "<connectionMode>manual</connectionMode><MSM><security><authEncryption><authentication>" + network.Authentication +
                "</authentication><encryption>" + network.Encryption + "</encryption><useOneX>false</useOneX></authEncryption>" +
                shared + "</security></MSM></WLANProfile>";
        }

        private sealed class Client : IDisposable
        {
            internal readonly IntPtr Handle;
            internal Client() { uint version; Check(WlanOpenHandle(2, IntPtr.Zero, out version, out Handle)); }
            public void Dispose() { WlanCloseHandle(Handle, IntPtr.Zero); }

            internal List<WifiNetwork> List(Guid adapter, string adapterName)
            {
                IntPtr ptr;
                Check(WlanGetAvailableNetworkList(Handle, ref adapter, 0, IntPtr.Zero, out ptr));
                try {
                    var result = new List<WifiNetwork>();
                    foreach (var n in ReadList<Network>(ptr)) {
                        if (n.Ssid.Length == 0 || n.Ssid.Length > 32) continue;
                        string hex = BitConverter.ToString(n.Ssid.Bytes, 0, (int)n.Ssid.Length).Replace("-", "");
                        string name = Encoding.UTF8.GetString(n.Ssid.Bytes, 0, (int)n.Ssid.Length);
                        if (String.IsNullOrWhiteSpace(name) || name.Any(Char.IsControl) || name.IndexOf('\ufffd') >= 0) name = hex;
                        result.Add(new WifiNetwork { Adapter = adapter, AdapterName = adapterName, Hex = hex, Name = name,
                            Profile = (n.Flags & 2) == 0 ? null : n.Profile, Signal = (int)n.Signal,
                            Connected = (n.Flags & 1) != 0, Connectable = n.Connectable && n.BssType == 1,
                            Authentication = n.Auth == 1 && !n.Secure ? "open" : n.Auth == 4 ? "WPAPSK" :
                                n.Auth == 7 ? "WPA2PSK" : n.Auth == 9 ? "WPA3SAE" : n.Auth == 10 ? "OWE" : null,
                            Encryption = n.Cipher == 0 && !n.Secure ? "none" : n.Cipher == 2 ? "TKIP" : n.Cipher == 4 ? "AES" : null });
                    }
                    return result;
                } finally { WlanFreeMemory(ptr); }
            }
        }

        private static IEnumerable<T> ReadList<T>(IntPtr address) where T : struct
        {
            int count = Marshal.ReadInt32(address), size = Marshal.SizeOf(typeof(T));
            for (int i = 0; i < count; ++i)
                yield return (T)Marshal.PtrToStructure(IntPtr.Add(address, checked(8 + i * size)), typeof(T));
        }
        private static void Check(uint error) { if (error != 0) throw new Win32Exception((int)error); }

        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct Interface {
            public Guid Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Name;
            public uint State;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Ssid {
            public uint Length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=32)] public byte[] Bytes;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct Network {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Profile;
            public Ssid Ssid;
            public uint BssType, BssidCount;
            [MarshalAs(UnmanagedType.Bool)] public bool Connectable;
            public uint Reason, PhyCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=8)] public uint[] PhyTypes;
            [MarshalAs(UnmanagedType.Bool)] public bool MorePhyTypes;
            public uint Signal;
            [MarshalAs(UnmanagedType.Bool)] public bool Secure;
            public uint Auth, Cipher, Flags, Reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] internal struct Connection {
            public uint Mode;
            [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
            public IntPtr Ssid, Bssids;
            public uint BssType, Flags;
        }
        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint actual, out IntPtr handle);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr handle, ref Guid adapter, IntPtr ssid, IntPtr ieData, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid adapter, uint flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanConnect(IntPtr handle, ref Guid adapter, ref Connection parameters, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(IntPtr handle, ref Guid adapter, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr pointer);
    }
}
