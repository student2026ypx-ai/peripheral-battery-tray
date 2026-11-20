using System;
using System.Linq;

namespace PeripheralBattery {
    // M HUB's inverted RealTek feature reports. The receiver reports the paired model separately.
    internal static class MchoseMouse {
        const byte ShortReport = 0x11, LongReport = 0x12;
        const string L7 = "MCHOSE L7 Pro", M7 = "MCHOSE M7 Ultra";

        internal static string PairedKey(HidInfo info, string product) {
            return WindowsDevices.PrivateKey(BatteryReader.UsbDeviceKey(info, "mouse") + "|" + product);
        }

        internal static bool MatchesKey(HidInfo info, string key) {
            return PairedKey(info, L7) == key || PairedKey(info, M7) == key;
        }

        internal static byte[] BuildRequest(byte report, byte command) {
            var frame = new byte[65];
            frame[0] = report;
            int payloadLength = report == ShortReport ? 20 : 64;
            for (int i = 1; i <= payloadLength; i++) frame[i] = 0xFF;
            frame[1] = (byte)(command ^ 0xFF);
            return frame;
        }

        internal static byte[] Decode(byte[] frame, byte report, byte command) {
            int length = report == ShortReport ? 19 : 63;
            if (frame == null || frame.Length < length + 2 || frame[0] != report ||
                frame[1] != (command ^ 0xFF) || !frame.Skip(2).Take(length).Any(value => value != 0)) return null;
            var data = new byte[length];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(frame[i + 2] ^ 0xFF);
            return data;
        }

        static byte[] Query(HidLink link, byte report, byte command) {
            var frame = BuildRequest(report, command);
            for (int attempt = 0; attempt < 3; attempt++) {
                var response = UsbMouseSupport.RequestFeature(link, frame, 35 + attempt * 35);
                var data = Decode(response, report, command);
                if (data != null) return data;
            }
            return null;
        }

        internal static UsbMousePower ParsePower(byte[] data, UsbMouseModel model) {
            if (data == null || data.Length < 11 || UsbMouseSupport.LittleWord(data, 0) != 0x5253)
                return null;
            int product = UsbMouseSupport.LittleWord(data, 2);
            string name = product == 0x00B0 ? L7 : product == 0x0031 ? M7 : null;
            if (name == null || (model.Receiver && (data[8] & 8) == 0) ||
                (!model.Receiver && product != model.Product)) return null;
            return new UsbMousePower {
                Name = name, Percent = data[9] <= 100 ? (int?)data[9] : null,
                Charging = data[10] <= 1 ? (bool?)(data[10] == 1) : null
            };
        }

        internal static UsbMousePower ReadPower(HidLink link, UsbMouseModel model) {
            var data = Query(link, ShortReport, 6);
            if (data != null && UsbMouseSupport.LittleWord(data, 0) == 0) {
                // An idle receiver can acknowledge the request before the paired mouse answers.
                System.Threading.Thread.Sleep(120);
                data = Query(link, ShortReport, 6);
            }
            return ParsePower(data, model);
        }

        internal static MouseDpiState ParseDpi(byte[] config, bool wireless) {
            if (config == null || config.Length < 63) return null;
            int active = config[wireless ? 2 : 1] & 15, count = config[16];
            if (count < 1 || count > 6 || active >= count) return null;
            var x = new int[count];
            var y = new int[count];
            for (int stage = 0; stage < count; stage++) {
                x[stage] = UsbMouseSupport.LittleWord(config, 4 + stage * 2);
                y[stage] = UsbMouseSupport.LittleWord(config, 51 + stage * 2);
                if (x[stage] == 0 || x[stage] == 65535 || y[stage] == 65535) return null;
                if (y[stage] == 0) y[stage] = x[stage];
            }
            return new MouseDpiState { Mode = 1, CanSwitch = false, Index = active, Profile = config[0],
                Current = x[active], Presets = x, VerticalPresets = y };
        }

        internal static MouseDpiState ReadDpi(HidLink link, UsbMouseModel model, string key) {
            var paired = ReadPower(link, model);
            if (paired == null || PairedKey(link.Info, paired.Name) != key) return null;
            // Do not change the profile to wake up an idle configuration channel.
            return ParseDpi(Query(link, LongReport, 0x67), model.Receiver);
        }
    }
}
