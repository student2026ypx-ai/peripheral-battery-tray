using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PeripheralBattery {
    [StructLayout(LayoutKind.Sequential)]
    internal struct DevicePropertyKey {
        public Guid Set;
        public uint Id;

        public DevicePropertyKey(string set, uint id) {
            Set = new Guid(set);
            Id = id;
        }
    }

    internal sealed partial class WindowsAudioOutputs {
        const int Render = 0;
        const int Multimedia = 1;
        const uint ActiveState = 1;
        static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        internal static readonly DevicePropertyKey ContainerKey = new DevicePropertyKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
        static readonly DevicePropertyKey InstanceIdKey = new DevicePropertyKey("78C34FC8-104A-4ACA-9EA4-524D52996E57", 256);
        static readonly DevicePropertyKey FriendlyName = new DevicePropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static readonly DevicePropertyKey FormFactor = new DevicePropertyKey("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E", 0);

        public readonly HashSet<Guid> ActiveContainers = new HashSet<Guid>();
        internal readonly Dictionary<Guid, PlaybackDevice> HeadsetOutputs = new Dictionary<Guid, PlaybackDevice>();
        public Guid DefaultContainer { get; private set; }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDeviceEnumerator {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out IDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IAudioDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDeviceCollection {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IAudioDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioDevice {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out DevicePropertyKey key);
            [PreserveSig] int GetValue(ref DevicePropertyKey key, out PropertyValue value);
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropertyValue {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Data;
            [FieldOffset(8)] public uint Unsigned;
        }

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropertyValue value);

        public static WindowsAudioOutputs Read(Func<string, Guid> containerForInstance) {
            var outputs = new WindowsAudioOutputs();
            IDeviceEnumerator enumerator = null;
            IDeviceCollection devices = null;
            IAudioDevice defaultDevice = null;
            try {
                enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
                if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out defaultDevice) == 0) {
                    PlaybackDevice playback;
                    outputs.DefaultContainer = GetContainer(defaultDevice, containerForInstance, out playback);
                    if (outputs.DefaultContainer != Guid.Empty) outputs.ActiveContainers.Add(outputs.DefaultContainer);
                    outputs.RecordHeadsetOutput(outputs.DefaultContainer, playback);
                }

                uint count;
                if (enumerator.EnumAudioEndpoints(Render, ActiveState, out devices) != 0 || devices.GetCount(out count) != 0)
                    return outputs;
                for (uint i = 0; i < count; i++) {
                    IAudioDevice device;
                    if (devices.Item(i, out device) != 0) continue;
                    try {
                        PlaybackDevice playback;
                        Guid container = GetContainer(device, containerForInstance, out playback);
                        if (container != Guid.Empty) outputs.ActiveContainers.Add(container);
                        outputs.RecordHeadsetOutput(container, playback);
                    } finally {
                        Marshal.ReleaseComObject(device);
                    }
                }
            } catch (COMException) {
                // The Bluetooth radio still supplies connection state when audio enumeration is unavailable.
            } finally {
                if (devices != null) Marshal.ReleaseComObject(devices);
                if (defaultDevice != null) Marshal.ReleaseComObject(defaultDevice);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            return outputs;
        }

        internal void RecordHeadsetOutput(Guid container, PlaybackDevice playback) {
            if (container == Guid.Empty || container == WindowsDevices.SystemContainer || playback == null || !playback.IsHeadset)
                return;
            if (!HeadsetOutputs.ContainsKey(container)) HeadsetOutputs.Add(container, playback);
        }

        static Guid GetContainer(IAudioDevice device, Func<string, Guid> containerForInstance, out PlaybackDevice playback) {
            playback = null;
            IPropertyStore store;
            if (device.OpenPropertyStore(0, out store) == 0) {
                try {
                    playback = DescribePlaybackDevice(device, store);
                    Guid container = ReadContainer(store);
                    if (container != Guid.Empty) return container;
                    string instanceId = ReadInstanceId(store);
                    if (!string.IsNullOrEmpty(instanceId)) {
                        container = containerForInstance(instanceId);
                        if (container != Guid.Empty) return container;
                    }
                } finally {
                    Marshal.ReleaseComObject(store);
                }
            }

            string endpointId;
            if (device.GetId(out endpointId) != 0 || string.IsNullOrEmpty(endpointId)) return Guid.Empty;
            return containerForInstance(@"SWD\MMDEVAPI\" + endpointId);
        }

        static PlaybackDevice DescribePlaybackDevice(IAudioDevice device, IPropertyStore store) {
            string id;
            if (device.GetId(out id) < 0 || String.IsNullOrEmpty(id)) return null;
            string name = TextProperty(store, FriendlyName);
            return String.IsNullOrWhiteSpace(name) ? null : new PlaybackDevice(id, name, NumberProperty(store, FormFactor));
        }

        static string TextProperty(IPropertyStore store, DevicePropertyKey key) {
            PropertyValue value;
            if (store.GetValue(ref key, out value) < 0) return null;
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Data) : null; }
            finally { PropVariantClear(ref value); }
        }

        static uint NumberProperty(IPropertyStore store, DevicePropertyKey key) {
            PropertyValue value;
            if (store.GetValue(ref key, out value) < 0) return 10;
            try { return value.Type == 19 ? value.Unsigned : 10; }
            finally { PropVariantClear(ref value); }
        }

        static Guid ReadContainer(IPropertyStore store) {
            var key = ContainerKey;
            PropertyValue value;
            if (store.GetValue(ref key, out value) != 0) return Guid.Empty;
            try {
                if (value.Type == 72 && value.Data != IntPtr.Zero)
                    return (Guid)Marshal.PtrToStructure(value.Data, typeof(Guid));
                return Guid.Empty;
            } finally {
                PropVariantClear(ref value);
            }
        }

        static string ReadInstanceId(IPropertyStore store) {
            var key = InstanceIdKey;
            PropertyValue value;
            if (store.GetValue(ref key, out value) != 0) return null;
            try {
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Data) : null;
            } finally {
                PropVariantClear(ref value);
            }
        }
    }
}
