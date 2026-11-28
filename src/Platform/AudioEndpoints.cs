using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lunaris
{
    internal sealed class AudioEndpoint
    {
        internal string Id, Name;
        internal bool Current, Muted;
        internal int Volume = -1;
    }

    internal static class AudioEndpoints
    {
        internal enum Flow { Output, Input }
        private enum Role { Console, Multimedia, Communications }

        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid PolicyClass = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
        private const int Active = 1;
        internal sealed class Snapshot
        {
            // Null means Windows could not enumerate this direction. An empty
            // array means enumeration succeeded and no active devices exist.
            internal AudioEndpoint[] Output, Input;
        }

        internal static Snapshot Read()
        {
            var result = new Snapshot();
            WithDevices(devices => {
                result.Output = Enumerate(devices, Flow.Output);
                result.Input = Enumerate(devices, Flow.Input);
            });
            return result;
        }

        private static AudioEndpoint[] Enumerate(IDevices devices, Flow flow)
        {
            ICollection collection = null;
            try {
                string current = DefaultId(devices, flow, Role.Multimedia);
                devices.EnumAudioEndpoints(flow, Active, out collection);
                uint count; collection.GetCount(out count);
                var list = new List<AudioEndpoint>();
                for (uint i = 0; i < count; i++) {
                    IDevice device = null;
                    try {
                        collection.Item(i, out device);
                        string id; device.GetId(out id);
                        string name = FriendlyName(device);
                        var item = new AudioEndpoint { Id = id, Name = String.IsNullOrWhiteSpace(name) ? id : name, Current = SameId(current, id) };
                        if (item.Current) ReadLevel(device, item);
                        list.Add(item);
                    } catch (COMException) { /* Hot unplug during enumeration. */ }
                    finally { Release(device); }
                }
                return list.ToArray();
            } catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally { Release(collection); }
        }

        private static string FriendlyName(IDevice device)
        {
            IPropertyStore store = null;
            var value = new PropVariant();
            try {
                device.OpenPropertyStore(0, out store);
                var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Number = 14 };
                store.GetValue(ref key, ref value);
                return value.Type == 31 ? Marshal.PtrToStringUni(value.String) : null;
            } catch (COMException) { return null; }
            finally { PropVariantClear(ref value); Release(store); }
        }

        private static bool SameId(string first, string second)
        {
            return String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        internal static void ReadOutput(out int volume, out bool muted)
        {
            var state = new AudioEndpoint();
            WithDevices(devices => {
                IDevice device = null;
                try {
                    devices.GetDefaultAudioEndpoint(Flow.Output, Role.Multimedia, out device);
                    ReadLevel(device, state);
                } finally { Release(device); }
            });
            volume = state.Volume;
            muted = state.Muted;
        }

        private static void ReadLevel(IDevice device, AudioEndpoint state)
        {
            try {
                WithLevel(device, endpoint => {
                    float scalar; bool muted;
                    endpoint.GetMasterVolumeLevelScalar(out scalar);
                    endpoint.GetMute(out muted);
                    state.Volume = (int)Math.Round(Math.Min(1, Math.Max(0, scalar)) * 100);
                    state.Muted = muted;
                });
            } catch (COMException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static bool SetVolume(string deviceId, int percent)
        {
            return SetLevel(deviceId, endpoint => endpoint.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, percent)) / 100f, IntPtr.Zero));
        }

        internal static bool SetMuted(string deviceId, bool muted)
        {
            return SetLevel(deviceId, endpoint => endpoint.SetMute(muted, IntPtr.Zero));
        }

        private static bool SetLevel(string deviceId, Action<IEndpointVolume> change)
        {
            if (String.IsNullOrEmpty(deviceId)) return false;
            return WithDevices(devices => {
                IDevice device = null;
                try { devices.GetDevice(deviceId, out device); WithLevel(device, change); }
                finally { Release(device); }
            });
        }

        private static void WithLevel(IDevice device, Action<IEndpointVolume> action)
        {
            object volume = null;
            try {
                Guid iid = typeof(IEndpointVolume).GUID;
                device.Activate(ref iid, 1, IntPtr.Zero, out volume);
                action((IEndpointVolume)volume);
            } finally { Release(volume); }
        }

        private static string DefaultId(IDevices devices, Flow flow, Role role)
        {
            IDevice device = null;
            try {
                devices.GetDefaultAudioEndpoint(flow, role, out device);
                string id; device.GetId(out id);
                return id;
            } catch (COMException ex) {
                if (ex.ErrorCode != unchecked((int)0x80070490)) throw;
                return null;
            } finally { Release(device); }
        }

        internal static bool Select(Flow flow, string id)
        {
            if (String.IsNullOrEmpty(id)) return false;
            bool applied = false;
            bool accessed = WithDevices(devices => {
                IDevice device = null;
                object policy = null;
                try {
                    devices.GetDevice(id, out device);
                    uint state; Flow actualFlow;
                    device.GetState(out state);
                    ((IEndpoint)device).GetDataFlow(out actualFlow);
                    if (actualFlow != flow || state != Active) return;
                    string console = DefaultId(devices, flow, Role.Console);
                    string media = DefaultId(devices, flow, Role.Multimedia);
                    if (SameId(console, id) && SameId(media, id)) { applied = true; return; }
                    policy = Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyClass, true));
                    var config = (IPolicyConfig)policy;
                    try {
                        if (!SameId(console, id)) config.SetDefaultEndpoint(id, Role.Console);
                        if (!SameId(media, id)) config.SetDefaultEndpoint(id, Role.Multimedia);
                        applied = SameId(DefaultId(devices, flow, Role.Console), id) && SameId(DefaultId(devices, flow, Role.Multimedia), id);
                    } finally {
                        if (!applied) {
                            RestoreIfChanged(devices, config, flow, Role.Console, console, id);
                            RestoreIfChanged(devices, config, flow, Role.Multimedia, media, id);
                        }
                    }
                } finally { Release(policy); Release(device); }
            });
            return accessed && applied;
        }

        private static void RestoreIfChanged(IDevices devices, IPolicyConfig policy, Flow flow, Role role, string previous, string selected)
        {
            try {
                if (previous != null && !SameId(previous, selected) && SameId(DefaultId(devices, flow, role), selected))
                    policy.SetDefaultEndpoint(previous, role);
            } catch (COMException) { /* The previous endpoint may have disconnected. */ }
            catch (UnauthorizedAccessException) { }
        }

        private static bool WithDevices(Action<IDevices> action)
        {
            object devices = null;
            try {
                devices = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
                action((IDevices)devices);
                return true;
            } catch (COMException) { return false; }
            catch (InvalidCastException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally { Release(devices); }
        }

        private static void Release(object value) { if (value != null) Marshal.ReleaseComObject(value); }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDevices {
            void EnumAudioEndpoints(Flow flow, uint state, out ICollection collection);
            void GetDefaultAudioEndpoint(Flow flow, Role role, out IDevice device);
            void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICollection { void GetCount(out uint count); void Item(uint index, out IDevice device); }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDevice {
            void Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            void OpenPropertyStore(uint access, out IPropertyStore store);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out uint state);
        }
        [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEndpoint { void GetDataFlow(out Flow flow); }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore {
            void GetCount(); void GetAt(); void GetValue(ref PropertyKey key, ref PropVariant value);
        }
        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Number; }
        [StructLayout(LayoutKind.Explicit, Size=24)] private struct PropVariant {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr String;
        }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEndpointVolume {
            void RegisterControlChangeNotify(); void UnregisterControlChangeNotify(); void GetChannelCount();
            void SetMasterVolumeLevel(); void SetMasterVolumeLevelScalar(float value, IntPtr context); void GetMasterVolumeLevel();
            void GetMasterVolumeLevelScalar(out float value);
            void SetChannelVolumeLevel(); void SetChannelVolumeLevelScalar(); void GetChannelVolumeLevel(); void GetChannelVolumeLevelScalar();
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool value, IntPtr context); void GetMute([MarshalAs(UnmanagedType.Bool)] out bool value);
        }
        // Windows policy client has no MMDevice API equivalent for changing the
        // system defaults. Query its exact interface, and surface failure in the menu.
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig {
            void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
            void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
            void GetPropertyValue(); void SetPropertyValue();
            void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        }
    }
}
