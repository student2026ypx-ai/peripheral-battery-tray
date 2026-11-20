using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    // Sinowealth keyboards. The same receiver ID is shared by more than one model.
    internal static class MchoseKeyboard {
        const byte RadioReport = 0x13, CableReport = 9;
        const int RadioSize = 20, CableSize = 520, Chunk = 14;
        const string G98 = "MCHOSE G98 V2", UT98 = "MCHOSE UT98";
        static readonly Dictionary<byte, int> BrightnessOffsets = new Dictionary<byte, int> {
            {1,85}, {2,87}, {3,89}, {4,91}, {5,93}, {7,97}, {8,99},
            {10,103}, {11,105}, {12,107}, {14,111}, {15,113}, {16,115}, {17,117}
        };

        internal static bool IsProduct(ushort vendor, ushort product) {
            return vendor == 0x41E4 && (product == 0x2004 || product == 0x2203);
        }

        internal static bool IsWired(HidInfo info) { return info.Pid == 0x2203; }

        internal static bool IsChannel(HidInfo info) {
            return info.Transport == "usb" && IsProduct(info.Vid, info.Pid) &&
                (IsWired(info) ? info.FeatureLength == CableSize :
                    info.InputLength == RadioSize && info.OutputLength == RadioSize && info.UsagePage >= 0xFF00);
        }

        internal static bool SupportsLighting(string name) { return name == G98 || name == UT98; }

        internal static byte[] RadioRequest(byte command) {
            var request = new byte[RadioSize];
            request[0] = RadioReport;
            request[1] = command;
            request[2] = 1;
            request[19] = (byte)(RadioReport + command + 1);
            return request;
        }

        internal static byte[] CableRequest(byte command, byte subcommand, int count) {
            var request = new byte[CableSize];
            request[0] = CableReport;
            request[1] = command;
            request[2] = subcommand;
            request[4] = 1;
            request[6] = (byte)count;
            request[7] = (byte)(count >> 8);
            return request;
        }

        internal static byte[] ParseCable(byte[] frame, byte command, int length) {
            if (frame == null || frame.Length != CableSize || frame[0] != CableReport ||
                (frame[1] != command && frame[1] != (command & 0x7F)) || length > CableSize - 8) return null;
            return frame.Skip(8).Take(length).ToArray();
        }

        internal static bool MatchesRadio(byte[] frame, byte command, int pages) {
            if (frame == null || frame.Length != RadioSize || frame[0] != RadioReport ||
                frame[1] != command || frame[2] < 1 || frame[2] > pages || frame[3] >= frame[2]) return false;
            // Reject the echoed query and bad checksums when the firmware includes one.
            return !frame.SequenceEqual(RadioRequest(command)) &&
                (frame[19] == 0 || (frame.Take(19).Sum(value => (int)value) & 255) == frame[19]);
        }

        static bool CanReadRadio(HidLink link) {
            return HidNative.HasReport(link.Handle, 0, RadioReport, RadioSize) &&
                HidNative.HasReport(link.Handle, 1, RadioReport, RadioSize);
        }

        static byte[] ReadBlock(HidLink link, byte radioCommand, byte cableCommand, byte subcommand, int length) {
            if (IsWired(link.Info)) {
                if (!link.WriteFeature(CableRequest(cableCommand, subcommand, length))) return null;
                Thread.Sleep(35);
                return ParseCable(link.ReadFeature(CableReport), cableCommand, length);
            }
            if (!CanReadRadio(link)) return null;
            link.Write(RadioRequest(radioCommand));
            int maxPages = (length + Chunk - 1) / Chunk;
            var pieces = new byte[maxPages][];
            var timer = Stopwatch.StartNew();
            for (int received = 0, count = 0; timer.ElapsedMilliseconds < 850;) {
                var response = link.ReadReply(frame => MatchesRadio(frame, radioCommand, maxPages), 180);
                if (response == null) continue;
                if (count != 0 && count != response[2]) return null;
                count = response[2];
                int page = response[3];
                if (pieces[page] != null) continue;
                pieces[page] = response.Skip(5).Take(Chunk).ToArray();
                if (++received == count)
                    return count * Chunk < length ? null : pieces.Take(count).SelectMany(piece => piece).Take(length).ToArray();
            }
            return null;
        }

        internal static string Model(byte[] data) {
            if (data == null || data.Length != 6 || data[0] != 0x0A || data.Skip(1).Take(4).Any(value => value != 0))
                return null;
            if (data[5] == 0x18) return G98;
            if (data[5] == 0x2B) return UT98;
            return null;
        }

        static string ReadModel(HidLink link) { return Model(ReadBlock(link, 5, 0x82, 1, 6)); }

        static string DeviceKey(HidInfo info, string model) {
            return WindowsDevices.PrivateKey(BatteryReader.UsbDeviceKey(info, "keyboard") + "|" + model);
        }

        internal static void ParsePower(byte[] data, BatteryState state) {
            if (data == null || data.Length != 2 || data[0] > 100) return;
            state.Percent = data[0];
            int charging = data[1] >> 4, full = data[1] & 15;
            if (charging <= 1 && full <= 1) state.Charging = charging == 1 && full == 0;
            state.Detail = "迈从键盘电量";
        }

        internal static BatteryState ReadPower(HidLink link) {
            string name = ReadModel(link);
            if (name == null) return null;
            var state = BatteryReader.UsbState(link.Info, "keyboard", name, IsWired(link.Info) ? "usb" : "wireless",
                "键盘暂未返回电量");
            state.DeviceKey = DeviceKey(link.Info, name);
            ParsePower(ReadBlock(link, 0x4A, 0x87, 0, 2), state);
            return state;
        }

        internal static KeyboardLightState ParseLighting(byte[] block, string key) {
            if (block == null || block.Length != 128 || block[32] != 0) return null;
            byte effect = block[33];
            int offset;
            if (effect == 0) return new KeyboardLightState { Key=key, CanSwitch=false };
            if (!BrightnessOffsets.TryGetValue(effect, out offset) || block[offset] > 20) return null;
            return new KeyboardLightState { Key=key, Effect=effect, Brightness=(byte)(block[offset] * 5), CanSwitch=false };
        }

        internal static KeyboardLightState ReadLighting(IEnumerable<HidInfo> devices, string key) {
            foreach (var info in devices.Where(IsChannel).Where(info => DeviceKey(info, G98) == key || DeviceKey(info, UT98) == key)) {
                var state = HidLink.Use(info, "keyboard", link => {
                    var model = ReadModel(link);
                    if (model == null || DeviceKey(info, model) != key) return null;
                    return ParseLighting(ReadBlock(link, 0x44, 0x84, 0, 128), key);
                });
                if (state != null) return state;
            }
            return null;
        }
    }
}
