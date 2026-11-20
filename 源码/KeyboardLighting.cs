using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace PeripheralBattery {
    internal sealed class KeyboardLightState {
        internal string Key;
        internal byte Profile, Effect, Brightness;
        internal bool CanSwitch = true;
        internal bool Enabled { get { return Effect != 0 && Brightness != 0; } }
    }

    internal static class KeyboardLighting {
        const byte ReadProfiles = 4, ReadConfig = 5, WriteConfig = 6;
        const int SettingsLength = 56, EffectOffset = 8, BrightnessOffset = 9;

        internal static bool Supports(string name) {
            return String.Equals(name, "MCHOSE G87 V2 2.4G", StringComparison.OrdinalIgnoreCase) ||
                MchoseKeyboard.SupportsLighting(name);
        }

        static bool Matches(HidInfo info, string key) {
            if (!KeyboardProtocol.IsChannel(info) || info.Pid != 0x3033 || !Supports(info.Product)) return false;
            string identity = info.Container == Guid.Empty ? info.PhysicalId : info.Container.ToString("N");
            return WindowsDevices.PrivateKey("usb|keyboard|" + identity) == key;
        }

        internal static KeyboardLightState Query(string key, KeyboardLightState expected, bool? enable, string cacheDirectory, out bool applied) {
            applied = false;
            if (String.IsNullOrWhiteSpace(key)) return null;
            var devices = HidNative.Enumerate();
            var device = devices.FirstOrDefault(info => Matches(info, key));
            if (device == null) return MchoseKeyboard.ReadLighting(devices, key);
            try {
                using (var link = new HidLink(device)) {
                    byte? profile = ReadProfile(link);
                    if (!profile.HasValue) return null;
                    var state = ReadLighting(link, key, profile.Value);
                    if (state == null || !enable.HasValue || !SameState(expected, state)) return state;
                    if (state.Enabled == enable.Value) { applied = true; return state; }

                    byte effect = state.Effect, brightness = 0;
                    if (enable.Value) {
                        byte savedEffect, savedBrightness;
                        bool saved = ReadSavedEffect(cacheDirectory, state, out savedEffect, out savedBrightness);
                        if (effect == 0) effect = saved ? savedEffect : (byte)1;
                        if (state.Brightness > 0) brightness = state.Brightness;
                        else if (saved && savedEffect == effect && savedBrightness > 0) brightness = savedBrightness;
                        else {
                            // The G87 V2 expresses brightness as a percentage.
                            brightness = 50;
                        }
                    } else if (!SaveEffect(cacheDirectory, state)) {
                        // Do not discard the current effect if it cannot be remembered.
                        return state;
                    }

                    if (ReadProfile(link) != profile) return null;
                    // Zero brightness leaves the current effect (and its color,
                    // speed, etc.) in place. Only initialize the effect if it was off.
                    ushort offset = (ushort)(profile.Value * 64 + (effect == state.Effect ? BrightnessOffset : EffectOffset));
                    byte[] data = effect == state.Effect ? new[]{brightness} : new[]{effect, brightness};
                    var write = Packet(WriteConfig, offset, data.Length, data);
                    // Acknowledgements are only a hint. Trust a fresh configuration readback.
                    Exchange(link, write, WriteConfig, offset, data.Length, true, 400);
                    state = ReadLighting(link, key, profile.Value);
                    applied = state != null && state.Effect == effect && state.Brightness == brightness && state.Enabled == enable.Value;
                    return state;
                }
            } catch (Win32Exception) { return null; }
            catch (TimeoutException) { return null; }
        }

        static bool SameState(KeyboardLightState first, KeyboardLightState second) {
            return first != null && second != null && first.Key == second.Key && first.Profile == second.Profile &&
                first.Effect == second.Effect && first.Brightness == second.Brightness;
        }

        static byte? ReadProfile(HidLink link) {
            var bytes = Exchange(link, KeyboardProtocol.BuildRead(ReadProfiles, 0, 65), ReadProfiles, 0, SettingsLength, false);
            if (bytes == null || bytes.Length < 6 || bytes[1] == 0 || bytes[1] > 4 || bytes[0] >= bytes[1] || bytes[2 + bytes[0]] >= 4)
                return null;
            return bytes[2 + bytes[0]];
        }

        static KeyboardLightState ReadLighting(HidLink link, string key, byte profile) {
            ushort offset = (ushort)(profile * 64);
            var bytes = Exchange(link, KeyboardProtocol.BuildRead(ReadConfig, offset, 65), ReadConfig, offset, SettingsLength, false);
            if (bytes == null || bytes.Length <= BrightnessOffset || bytes[BrightnessOffset] > 100 || bytes[EffectOffset] == 0xFF) return null;
            return new KeyboardLightState { Key = key, Profile = profile, Effect = bytes[EffectOffset], Brightness = bytes[BrightnessOffset] };
        }

        static byte[] Packet(byte command, ushort offset, int length, byte[] data) {
            var report = new byte[65];
            report[1] = 0x55; report[2] = command; report[5] = (byte)length;
            report[6] = (byte)offset; report[7] = (byte)(offset >> 8);
            int checksum = length + report[6] + report[7];
            for (int i = 0; i < length; i++) { report[9 + i] = data[i]; checksum += data[i]; }
            report[4] = (byte)checksum;
            return report;
        }

        static byte[] Exchange(HidLink link, byte[] report, byte command, ushort offset, int maxLength, bool ack, int timeout = 950) {
            link.Write(report);
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeout) {
                var raw = link.Read(60);
                if (KeyboardProtocol.IsRejected(raw, command)) return null;
                if (!ack) {
                    byte[] data;
                    if (KeyboardProtocol.TryResponse(raw, command, offset, out data) && data.Length <= maxLength) return data;
                } else {
                    var d = KeyboardProtocol.Payload(raw);
                    if (d != null && d.Length >= 8 && d[0] == 0xAA && d[1] == command && d[2] == 0 &&
                        d[4] <= maxLength && d[5] == (byte)offset && d[6] == (byte)(offset >> 8)) return new byte[0];
                }
            }
            return null;
        }

        static string CachePath(string directory, KeyboardLightState state) {
            string localKey = WindowsDevices.PrivateKey(state.Key).Substring(0, 20);
            return Path.Combine(directory, "灯光记忆-" + localKey + "-" + state.Profile + ".txt");
        }

        static bool SaveEffect(string directory, KeyboardLightState state) {
            try {
                File.WriteAllText(CachePath(directory, state), state.Effect + "," + state.Brightness, Encoding.UTF8);
                return true;
            } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }

        static bool ReadSavedEffect(string directory, KeyboardLightState state, out byte effect, out byte brightness) {
            effect = brightness = 0;
            try {
                var file = new FileInfo(CachePath(directory, state));
                if (!file.Exists || file.Length > 32) return false;
                var data = File.ReadAllText(file.FullName).Split(',');
                return data.Length == 2 && Byte.TryParse(data[0], out effect) && effect != 0 && effect != 0xFF &&
                    Byte.TryParse(data[1], out brightness) && brightness <= 100;
            } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
    }
}
