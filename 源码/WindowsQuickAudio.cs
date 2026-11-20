using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PeripheralBattery {
    internal sealed class PlaybackDevice {
        public readonly string Id, Name;
        public readonly uint Form;
        readonly bool headsetReceiver;
        public PlaybackDevice(string id, string name, uint form, bool headsetReceiver = false) {
            Id = id; Name = name; Form = form; this.headsetReceiver = headsetReceiver;
        }
        public bool IsHeadset {
            get {
                return headsetReceiver || Form == 3 || Form == 5 || Name.IndexOf("headphone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Name.IndexOf("headset", StringComparison.OrdinalIgnoreCase) >= 0 || Name.Contains("耳机") || Name.Contains("耳麥");
            }
        }
        public bool IsSpeaker { get { return !IsHeadset && (Form == 1 || Form == 2 || Form == 8 || Form == 9); } }
    }

    internal sealed class AudioControlsState {
        public string DefaultOutput, MicrophoneId;
        public bool MicrophoneMuted, CanSwitch;
        public PlaybackDevice[] Outputs = new PlaybackDevice[0];

        public PlaybackDevice Preferred(bool headset, string previous) {
            PlaybackDevice first = null, current = null;
            foreach (var output in Outputs) {
                if (headset ? !output.IsHeadset : !output.IsSpeaker) continue;
                if (String.Equals(output.Id, DefaultOutput, StringComparison.OrdinalIgnoreCase)) return output;
                if (String.Equals(output.Id, previous, StringComparison.OrdinalIgnoreCase)) current = output;
                if (first == null || (output.Form == 1 && first.Form != 1) ||
                    ((output.Form == 1) == (first.Form == 1) &&
                        String.Compare(output.Name, first.Name, StringComparison.CurrentCultureIgnoreCase) < 0)) first = output;
            }
            return current ?? first;
        }
    }

    internal interface IAudioControls : IDisposable {
        AudioControlsState Read(bool listOutputs);
        bool ToggleMicrophone(string expectedId);
        bool SwitchOutput(string id);
    }

    internal sealed partial class WindowsAudioOutputs {
        internal sealed class QuickAudio : IAudioControls {
            const int Capture = 1, ConsoleRole = 0;
            const uint InProcServer = 1;
            static readonly Guid PolicyClass = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
            IDeviceEnumerator enumerator;
            IAudioEndpointVolume microphone;
            string microphoneId;
            IAudioPolicy policy;
            bool policyChecked;
            PlaybackDevice[] outputs = new PlaybackDevice[0];

            IDeviceEnumerator Enumerator {
                get {
                    if (enumerator == null)
                        enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
                    return enumerator;
                }
            }

            IAudioPolicy Policy {
                get {
                    if (policyChecked) return policy;
                    policyChecked = true;
                    object obj = null;
                    try {
                        obj = Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyClass, true));
                        policy = obj as IAudioPolicy;
                    } catch (COMException) {
                        policy = null;
                    } finally {
                        if (policy == null) Release(obj);
                    }
                    return policy;
                }
            }

            public AudioControlsState Read(bool listOutputs) {
                var state = new AudioControlsState();
                try {
                    state.DefaultOutput = DefaultId(Render, ConsoleRole);
                    // BlueTooth connections can arrive while the panel is open.
                    if (listOutputs) outputs = ReadPlayback();
                    state.Outputs = outputs;
                    state.CanSwitch = Policy != null;
                } catch (COMException) {
                    outputs = new PlaybackDevice[0];
                }
                try {
                    if (BindMicrophone() && microphone.GetMute(out state.MicrophoneMuted) >= 0) state.MicrophoneId = microphoneId;
                } catch (COMException) { ReleaseMicrophone(); }
                return state;
            }

            public bool ToggleMicrophone(string expectedId) {
                try {
                    bool muted, after;
                    return !String.IsNullOrEmpty(expectedId) && BindMicrophone() &&
                        String.Equals(expectedId, microphoneId, StringComparison.OrdinalIgnoreCase) &&
                        microphone.GetMute(out muted) >= 0 && microphone.SetMute(!muted, IntPtr.Zero) >= 0 &&
                        microphone.GetMute(out after) >= 0 && after == !muted;
                } catch (COMException) { return false; }
            }

            public bool SwitchOutput(string id) {
                if (String.IsNullOrEmpty(id)) return false;
                // The list is re-read on click. Never issue a switch to an input
                // endpoint or a device that has disappeared since the last paint.
                try {
                    bool available = false;
                    foreach (var output in ReadPlayback())
                        if (String.Equals(output.Id, id, StringComparison.OrdinalIgnoreCase)) { available = true; break; }
                    if (!available) return false;
                    var setter = Policy;
                    if (setter == null) return false;
                    string beforeConsole = DefaultId(Render, ConsoleRole), beforeMedia = DefaultId(Render, Multimedia);
                    if (String.IsNullOrEmpty(beforeConsole) || String.IsNullOrEmpty(beforeMedia)) return false;
                    if (String.Equals(beforeConsole, id, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(beforeMedia, id, StringComparison.OrdinalIgnoreCase)) return true;

                    // Keep system and media output together. Leave the independent
                    // Windows communications role alone.
                    if (setter.SetDefaultEndpoint(id, ConsoleRole) < 0) return false;
                    if (setter.SetDefaultEndpoint(id, Multimedia) < 0) {
                        if (!String.Equals(beforeConsole, id, StringComparison.OrdinalIgnoreCase))
                            setter.SetDefaultEndpoint(beforeConsole, ConsoleRole);
                        return false;
                    }
                    return String.Equals(DefaultId(Render, ConsoleRole), id, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(DefaultId(Render, Multimedia), id, StringComparison.OrdinalIgnoreCase);
                } catch (COMException) { return false; }
            }

            internal string DefaultId(int flow, int role) {
                IAudioDevice device = null;
                try {
                    string id;
                    return Enumerator.GetDefaultAudioEndpoint(flow, role, out device) >= 0 && device != null &&
                        device.GetId(out id) >= 0 ? id : null;
                } finally { Release(device); }
            }

            PlaybackDevice[] ReadPlayback() {
                var found = new List<PlaybackDevice>();
                IDeviceCollection list = null;
                try {
                    uint count;
                    if (Enumerator.EnumAudioEndpoints(Render, ActiveState, out list) < 0 || list == null || list.GetCount(out count) < 0)
                        return found.ToArray();
                    var models = UsbHeadsetSupport.ConnectedModels();
                    for (uint i = 0; i < count; i++) {
                        IAudioDevice device = null;
                        try {
                            if (list.Item(i, out device) < 0 || device == null) continue;
                            PlaybackDevice playback;
                            Guid container = GetContainer(device, WindowsDevices.ContainerForInstance, out playback);
                            if (playback == null) continue;
                            found.Add(!playback.IsHeadset && models.ContainsKey(container)
                                ? new PlaybackDevice(playback.Id, playback.Name, playback.Form, true) : playback);
                        } finally { Release(device); }
                    }
                    return found.ToArray();
                } finally { Release(list); }
            }

            bool BindMicrophone() {
                IAudioDevice device = null;
                IntPtr pointer = IntPtr.Zero;
                try {
                    string id;
                    if (Enumerator.GetDefaultAudioEndpoint(Capture, ConsoleRole, out device) < 0 || device == null ||
                        device.GetId(out id) < 0 || String.IsNullOrEmpty(id)) { ReleaseMicrophone(); return false; }
                    if (microphone != null && String.Equals(microphoneId, id, StringComparison.OrdinalIgnoreCase)) return true;
                    ReleaseMicrophone();
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, InProcServer, IntPtr.Zero, out pointer) < 0 || pointer == IntPtr.Zero) return false;
                    microphone = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(pointer);
                    microphoneId = id;
                    return true;
                } catch (COMException) {
                    ReleaseMicrophone();
                    return false;
                } catch (InvalidCastException) {
                    ReleaseMicrophone();
                    return false;
                } finally {
                    if (pointer != IntPtr.Zero) Marshal.Release(pointer);
                    Release(device);
                }
            }

            static void Release(object instance) {
                if (instance != null) Marshal.ReleaseComObject(instance);
            }

            void ReleaseMicrophone() {
                Release(microphone);
                microphone = null;
                microphoneId = null;
            }

            public void Dispose() {
                ReleaseMicrophone();
                Release(policy); policy = null;
                Release(enumerator); enumerator = null;
            }

            // Default endpoint switching is not exposed by the public MMDevice
            // enumerator. This Windows desktop interface is optional and verified
            // with the public read API after each requested change.
            [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IAudioPolicy {
                // The preceding interface slots are intentionally never called.
                void Slot0(); void Slot1(); void Slot2(); void Slot3(); void Slot4();
                void Slot5(); void Slot6(); void Slot7(); void Slot8(); void Slot9();
                [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string endpoint, int role);
            }
        }
    }
}
