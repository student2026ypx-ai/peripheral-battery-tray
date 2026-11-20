using System;
using System.Runtime.InteropServices;

namespace PeripheralBattery {
    internal struct OutputVolumeState {
        public readonly string EndpointId;
        public readonly float Level;
        public readonly bool Muted;

        public OutputVolumeState(string endpointId, float level, bool muted) {
            EndpointId = endpointId;
            Level = level;
            Muted = muted;
        }

        public bool Available { get { return !String.IsNullOrEmpty(EndpointId); } }
        public int Percent { get { return (int)Math.Round(Level * 100.0f, MidpointRounding.AwayFromZero); } }
    }

    internal interface ISystemVolume : IDisposable {
        bool TryRead(out OutputVolumeState state);
        bool TrySet(string expectedEndpoint, float level, out OutputVolumeState state);
    }

    internal sealed partial class WindowsAudioOutputs {
        // This object is used only on the panel's UI thread. The panel checks the
        // default endpoint on each poll and write, without background COM callbacks.
        internal sealed class SystemVolume : ISystemVolume {
            const int ConsoleRole = 0;
            const uint InProcServer = 1;
            IDeviceEnumerator enumerator;
            IAudioEndpointVolume endpoint;
            string endpointId;

            public bool TryRead(out OutputVolumeState state) {
                return TryAccess(null, null, out state);
            }

            public bool TrySet(string expectedEndpoint, float level, out OutputVolumeState state) {
                if (String.IsNullOrEmpty(expectedEndpoint) || Single.IsNaN(level) || level < 0 || level > 1) {
                    state = default(OutputVolumeState);
                    return false;
                }
                return TryAccess(expectedEndpoint, level, out state);
            }

            bool TryAccess(string expectedEndpoint, float? target, out OutputVolumeState state) {
                state = default(OutputVolumeState);
                try {
                    BindDefaultOutput();
                    // If Windows switched outputs, show the new endpoint's level.
                    // A gesture begun on the previous device must not change this one.
                    if (target.HasValue && String.Equals(expectedEndpoint, endpointId, StringComparison.OrdinalIgnoreCase))
                        Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(target.Value, IntPtr.Zero));
                    float level;
                    bool muted;
                    Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out level));
                    Marshal.ThrowExceptionForHR(endpoint.GetMute(out muted));
                    if (Single.IsNaN(level) || level < 0 || level > 1) return false;
                    state = new OutputVolumeState(endpointId, level, muted);
                    return true;
                } catch (COMException) {
                    Dispose();
                    return false;
                } catch (InvalidCastException) {
                    Dispose();
                    return false;
                }
            }

            void BindDefaultOutput() {
                if (enumerator == null)
                    enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
                IAudioDevice device = null;
                IntPtr instance = IntPtr.Zero;
                try {
                    Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(Render, ConsoleRole, out device));
                    string id;
                    Marshal.ThrowExceptionForHR(device.GetId(out id));
                    if (endpoint != null && String.Equals(endpointId, id, StringComparison.OrdinalIgnoreCase)) return;

                    ReleaseEndpoint();
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(ref iid, InProcServer, IntPtr.Zero, out instance));
                    endpoint = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(instance);
                    endpointId = id;
                } finally {
                    if (instance != IntPtr.Zero) Marshal.Release(instance);
                    if (device != null) Marshal.ReleaseComObject(device);
                }
            }

            void ReleaseEndpoint() {
                if (endpoint != null) Marshal.ReleaseComObject(endpoint);
                endpoint = null;
                endpointId = null;
            }

            public void Dispose() {
                ReleaseEndpoint();
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                enumerator = null;
            }
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume {
            // Slots preceding GetMute are declared in Windows SDK vtable order.
            [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float decibels, IntPtr context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
            [PreserveSig] int GetMasterVolumeLevel(out float decibels);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, IntPtr context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        }
    }
}
