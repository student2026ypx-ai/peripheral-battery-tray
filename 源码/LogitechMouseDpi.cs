using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PeripheralBattery {
    internal sealed class MouseDpiState {
        public byte Mode;
        public ushort Profile;
        public int[] Presets = new int[0];
        public int[] VerticalPresets;
        // A provisional lower bound when only the active stage identifies how many are usable.
        public int ConfirmedStages;
        public int Index = -1;
        public int Current;
        public int CurrentVertical;
        public bool CanSwitch = true;
        public int VisibleStageCount { get { return ConfirmedStages == 0 ? Presets.Length : ConfirmedStages; } }

        public bool SameProfile(MouseDpiState other) {
            return other != null && Mode == other.Mode && Profile == other.Profile &&
                Presets.SequenceEqual(other.Presets) &&
                (VerticalPresets ?? Presets).SequenceEqual(other.VerticalPresets ?? other.Presets);
        }
    }

    internal sealed partial class LogitechInputBattery {
        const ushort OnboardProfiles = 0x8100, AdjustableDpi = 0x2201, ExtendedDpi = 0x2202;

        internal static bool SupportsDpi(string name) {
            if (String.IsNullOrWhiteSpace(name)) return false;
            string normalized = name.Trim();
            if (normalized.StartsWith("Logitech ",StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(9);
            else if (normalized.StartsWith("Logi ",StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(5);
            return new[]{"G304","G305","G502 HERO","G502 SE HERO","G502 X","G603","LIFT","G903","PRO X SUPERLIGHT","PRO WIRELESS",
                "G PRO WIRELESS","G PRO GAMING","MX MASTER 3S","MX MASTER 4"}.Any(
                model => String.Equals(normalized,model,StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(model+" ",StringComparison.OrdinalIgnoreCase));
        }

        static bool CanSwitchDpi(string name) {
            return String.Equals(name,"G304 X LIGHTSPEED",StringComparison.OrdinalIgnoreCase);
        }

        internal static MouseDpiState MouseDpi(string key, MouseDpiState expected, int stage, out bool applied,
                IEnumerable<HidInfo> devices = null) {
            applied = false;
            if (String.IsNullOrWhiteSpace(key)) return null;
            foreach (var group in (devices ?? HidNative.Enumerate()).Where(info => IsChannel(info) &&
                    (stage < 0 || info.Pid == 0xC0A7 || info.Pid == 0xC547))
                .GroupBy(info => info.PhysicalId, StringComparer.OrdinalIgnoreCase)) {
                var primary = group.OrderByDescending(info => info.InputLength).FirstOrDefault();
                var shortInfo = group.FirstOrDefault(info => info.InputLength == 7);
                if (primary == null) continue;
                try {
                    using (var link = new HidLink(primary)) {
                        HidLink companion = null;
                        if (shortInfo != null && shortInfo != primary) {
                            try { companion = new HidLink(shortInfo); }
                            catch (Win32Exception) {}
                        }
                        using (companion) {
                            bool receiver = IsReceiver(primary);
                            var service = new LogitechInputBattery(link, companion, "mouse", 10);
                            byte[] slots = receiver ? new byte[] {1, 2, 3, 4, 5, 6} : new byte[] {0xFF};
                            foreach (byte slot in slots) {
                                var mouse = service.IdentifySlot(slot, "mouse", receiver);
                                if (mouse == null || !SupportsDpi(mouse.Name)) continue;
                                bool bluetooth = primary.Transport == "bluetooth";
                                string deviceKey = bluetooth ? BluetoothInputBattery.DeviceKey(primary,"mouse") : mouse.DeviceKey;
                                if (deviceKey != key) continue;
                                if (!bluetooth && CanSwitchDpi(mouse.Name))
                                    return service.QueryDpi(slot, expected, stage, out applied);
                                return stage < 0 ? service.ReadCurrentDpi(slot) : null;
                            }
                        }
                    }
                } catch (Win32Exception) {} catch (TimeoutException) {}
            }
            return null;
        }

        MouseDpiState ReadCurrentDpi(byte slot) {
            byte feature = Feature(slot,AdjustableDpi);
            if (feature != 0) {
                var state = ParseCurrentDpi(Request(slot,feature,2,600,0),false);
                if (state != null) return state;
            }
            feature = Feature(slot,ExtendedDpi);
            return feature == 0 ? null : ParseCurrentDpi(Request(slot,feature,5,600,0),true);
        }

        internal static MouseDpiState ParseCurrentDpi(byte[] frame, bool extended) {
            if (frame == null || frame.Length < (extended ? 5 : 3) || frame[0] != 0) return null;
            int dpi = BigWord(frame,1);
            return dpi < 50 || dpi == 0xFFFF ? null : MouseDpiQueries.Current(dpi,dpi);
        }

        MouseDpiState QueryDpi(byte slot, MouseDpiState expected, int stage, out bool applied) {
            applied = false;
            byte profiles = Feature(slot, OnboardProfiles);
            if (profiles == 0) return null;
            var format = Request(slot, profiles, 0);
            var mode = Request(slot, profiles, 2);
            BatteryReader.Log("DPI profile descriptor "+(format==null?"none":BitConverter.ToString(format))+" mode "+(mode==null?"none":mode[0].ToString()));
            if (format == null || format.Length < 9 || format[0] != 1 || format[1] != 7 ||
                mode == null || (mode[0] != 1 && mode[0] != 2)) return null;

            var state = new MouseDpiState { Mode = mode[0], Presets = new int[0] };
            if (state.Mode == 2) {
                // Host mode has a live value but no readable hardware stage list.
                byte dpi = Feature(slot, ExtendedDpi);
                var report = dpi == 0 ? null : Request(slot, dpi, 5, 600, 0);
                if (report != null && report.Length >= 5) {
                    state.Current = BigWord(report, 1);
                    if (state.Current == 0) state.Current = BigWord(report, 3);
                }
                return state;
            }

            var active = Request(slot, profiles, 4);
            if (active == null || active.Length < 2) return null;
            BatteryReader.Log("DPI active sector "+BigWord(active,0));
            state.Profile = (ushort)BigWord(active, 0);
            if (state.Profile == 0 || state.Profile == 0xFFFF || active[0] > 1 ||
                (BigWord(format, 7) < 32)) return null;

            // Format 7 has five records at offset 4, each with little-endian
            // X and Y and one LOD byte. Zero and erased values mean empty slots.
            // Several Logitech mice keep the factory profile in ROM until written
            // by their vendor app. Consult its directory rather than inventing stages.
            ushort sector = state.Profile;
            var first = Request(slot, profiles, 5, 850, active[0], active[1], 0, 0);
            if (first == null && active[0] == 0 && active[1] <= 5) {
                var factory = Request(slot, profiles, 5, 650, 1, 0, 0, (byte)((active[1] - 1) * 4));
                if (factory != null && factory.Length >= 3 && factory[0] == 1 && factory[1] == active[1] && factory[2] == 1) {
                    sector = (ushort)BigWord(factory, 0);
                    first = Request(slot, profiles, 5, 650, factory[0], factory[1], 0, 0);
                }
            }
            var second = first == null ? null : Request(slot, profiles, 5, 650, (byte)(sector >> 8), (byte)sector, 0, 16);
            BatteryReader.Log("DPI sector bytes "+(first==null?0:first.Length)+" + "+(second==null?0:second.Length));
            if (first == null || first.Length != 16 || second == null || second.Length != 16) return null;
            state.Presets = ParseDpi(first.Concat(second).ToArray());
            if (!state.Presets.Any(n => n > 0)) return null;
            var index = Request(slot, profiles, 11);
            if (index != null && index[0] < state.Presets.Length && state.Presets[index[0]] > 0) {
                state.Index = index[0];
                state.Current = state.Presets[state.Index];
            }

            if (stage >= 0 && stage < state.Presets.Length && state.Presets[stage] > 0 && state.Index >= 0 && state.SameProfile(expected)) {
                // Select the active stage only; never rewrite the onboard profile.
                var confirmation = Request(slot, profiles, 4);
                if (confirmation == null || confirmation.Length < 2 || BigWord(confirmation, 0) != state.Profile) return null;
                if (state.Index != stage) Request(slot, profiles, 12, 650, (byte)stage);
                index = Request(slot, profiles, 11);
                if (index == null) index = Request(slot, profiles, 11);
                if (index != null && index[0] < state.Presets.Length && state.Presets[index[0]] > 0) {
                    state.Index = index[0];
                    state.Current = state.Presets[state.Index];
                    applied = state.Index == stage;
                }
            }
            return state;
        }

        static int BigWord(byte[] bytes, int offset) { return bytes[offset] << 8 | bytes[offset + 1]; }

        internal static int[] ParseDpi(byte[] data) {
            if (data == null || data.Length < 29) return new int[0];
            var values = new int[5];
            for (int index = 0; index < values.Length; index++) {
                int offset = 4 + 5 * index;
                int value = data[offset] | data[offset + 1] << 8;
                if (value > 0 && value != 0xFFFF) values[index] = value;
            }
            return values;
        }
    }
}
