using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Lunaris
{
    internal sealed class SystemStatus
    {
        internal bool Connected, Wireless, Muted;
        internal int Volume = -1, Battery = -1;
        internal bool OnAC;

        internal static SystemStatus Read()
        {
            var result = new SystemStatus();
            try {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    result.Connected = true;
                    result.Wireless |= nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                }
            } catch (NetworkInformationException) { }
            Power power;
            if (GetSystemPowerStatus(out power) && power.Flags != 255 && (power.Flags & 128) == 0 && power.Percent <= 100) {
                result.Battery = power.Percent;
                result.OnAC = power.AC == 1;
            }
            AudioEndpoints.ReadOutput(out result.Volume, out result.Muted);
            return result;
        }

        [StructLayout(LayoutKind.Sequential)] private struct Power {
            public byte AC, Flags, Percent, Saving;
            public int Remaining, Full;
        }
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out Power status);
    }
}
