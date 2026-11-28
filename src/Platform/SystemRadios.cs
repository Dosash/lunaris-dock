using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace Lunaris
{
    internal sealed class SystemRadio
    {
        internal string Kind, Name;
        internal bool? Enabled;
        private Radio device;

        // The Framework compiler uses the Windows 11 system metadata directly.
        private static Task<T> Complete<T>(IAsyncOperation<T> operation)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.Completed = (op, status) => {
                try { result.TrySetResult(op.GetResults()); }
                catch (Exception ex) { result.TrySetException(ex); }
                finally { op.Close(); }
            };
            return result.Task;
        }

        internal static async Task<List<SystemRadio>> Wireless()
        {
            var rows = new List<SystemRadio>();
            var radios = await Complete(Radio.GetRadiosAsync());
            foreach (var radio in radios.Where(r => r.Kind == RadioKind.Bluetooth || r.Kind == RadioKind.WiFi)) {
                var state = radio.State;
                rows.Add(new SystemRadio { device = radio, Kind = radio.Kind == RadioKind.Bluetooth ? "Bluetooth" : "WifiRadio",
                    Name = radio.Name, Enabled = state == RadioState.On || state == RadioState.Off ? state == RadioState.On : (bool?)null });
            }
            return rows;
        }

        internal async Task<bool> Set(bool enabled)
        {
            if (Kind == "Airplane") {
                return await Task.Run(() => {
                    var manager = Manager();
                    try {
                        manager.SetSystemRadioState(enabled ? 0 : 1);
                        return true;
                    } finally { Marshal.ReleaseComObject(manager); }
                });
            }
            // Windows may ask for consent here. Request access only on an explicit click.
            if (device == null || await Complete(Radio.RequestAccessAsync()) != RadioAccessStatus.Allowed) return false;
            return await Complete(device.SetStateAsync(enabled ? RadioState.On : RadioState.Off)) == RadioAccessStatus.Allowed;
        }

        internal static SystemRadio Airplane()
        {
            var row = new SystemRadio { Kind = "Airplane" };
            var manager = Manager();
            try {
                int supported, enabled, changeable, reason;
                manager.IsRMSupported(out supported);
                manager.GetSystemRadioState(out enabled, out changeable, out reason);
                if (supported != 0 && (enabled == 0 || enabled == 1)) row.Enabled = enabled == 0;
                return row;
            } finally { Marshal.ReleaseComObject(manager); }
        }

        // RmSvc owns the global radio state and restores individual radios when leaving airplane mode.
        private static IRadioManager Manager()
        {
            return (IRadioManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("581333F6-28DB-41BE-BC7A-FF201F12F3F6"), true));
        }

        [ComImport, Guid("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IRadioManager
        {
            void IsRMSupported(out int supported);
            void GetUIRadioInstances(out IntPtr instances);
            void GetSystemRadioState(out int enabled, out int uiState, out int reason);
            void SetSystemRadioState(int enabled);
        }
    }
}
