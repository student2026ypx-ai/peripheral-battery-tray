using System;

namespace PeripheralBattery {
    // Modern Razer mouse reports documented by OpenRazer and the OpenMouse codec.
    internal static class RazerMouse {
        const int ReportLength = 91;
        const int Arguments = 9, Checksum = 89;
        const byte Transaction = 0x1F;

        internal static bool IsChannel(HidInfo info) {
            return info.FeatureLength == ReportLength &&
                ((info.Pid != 0x00E5 && info.Pid != 0x00E6) ||
                    (info.UsagePage == 0x000C && info.Usage == 1 && ExpandedUsbBattery.OnInterface(info,3)));
        }

        internal static bool ReportsCharging(ushort product) {
            switch (product) {
                case 0x0094: // Orochi V2 receiver
                case 0x00E0:
                case 0x009C: // DeathAdder V2 X HyperSpeed
                case 0x00B4: // Naga V2 HyperSpeed
                case 0x00B8: // Viper V3 HyperSpeed
                case 0x00B9: // Basilisk V3 X HyperSpeed
                case 0x00D3: // Basilisk Mobile
                case 0x00D4:
                    return false;
                default:
                    return true;
            }
        }

        internal static byte[] BuildRequest(byte group, byte command, byte argumentCount, byte first = 0, byte transaction = Transaction) {
            var frame = new byte[ReportLength];
            frame[2] = transaction;
            frame[6] = argumentCount;
            frame[7] = group;
            frame[8] = command;
            frame[Arguments] = first;
            frame[Checksum] = Xor(frame);
            return frame;
        }

        static byte Xor(byte[] frame) {
            byte value = 0;
            for (int i = 3; i < Checksum; i++) value ^= frame[i];
            return value;
        }

        internal static bool Matches(byte[] frame, byte group, byte command, int minimumArgs, byte transaction = Transaction) {
            return frame != null && frame.Length == ReportLength && frame[0] == 0 && frame[1] == 2 &&
                frame[2] == transaction && frame[3] == 0 && frame[4] == 0 && frame[5] == 0 &&
                frame[6] >= minimumArgs && frame[6] <= 80 && frame[7] == group && frame[8] == command &&
                frame[Checksum] == Xor(frame);
        }

        static byte[] Query(HidLink link, byte group, byte command, byte count, byte first = 0) {
            var request = BuildRequest(group, command, count, first);
            var frame = UsbMouseSupport.RequestFeature(link, request, 35);
            if (frame != null && frame[1] == 1 && frame[2] == Transaction)
                frame = UsbMouseSupport.RequestFeature(link, request, 65);
            return frame;
        }

        internal static UsbMousePower ParsePower(byte[] battery, byte[] charging, byte transaction = Transaction) {
            if (!Matches(battery, 7, 0x80, 2, transaction)) return null;
            var result = new UsbMousePower { Percent = (battery[10] * 100 + 127) / 255 };
            if (Matches(charging, 7, 0x84, 2, transaction) && charging[10] <= 1) result.Charging = charging[10] != 0;
            return result;
        }

        internal static UsbMousePower ReadPower(HidLink link) {
            var charge = Query(link, 7, 0x80, 2);
            if (!Matches(charge, 7, 0x80, 2)) return null;
            return ParsePower(charge, ReportsCharging(link.Info.Pid) ? Query(link, 7, 0x84, 2) : null);
        }

        static int BigWord(byte[] buffer, int offset) {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }

        internal static MouseDpiState ParseStages(byte[] frame) {
            if (!Matches(frame, 4, 0x86, 3)) return null;
            int count = frame[11], active = frame[10] - 1;
            if (count < 1 || count > 5 || active < 0 || active >= count || frame[6] < 3 + count * 7) return null;
            var x = new int[count];
            var y = new int[count];
            for (int stage = 0; stage < count; stage++) {
                int item = 12 + stage * 7;
                x[stage] = BigWord(frame, item + 1);
                y[stage] = BigWord(frame, item + 3);
                if (x[stage] == 0 || y[stage] == 0) return null;
            }
            return new MouseDpiState { Mode = 1, CanSwitch = false, Current = x[active], Index = active,
                Presets = x, VerticalPresets = y };
        }

        internal static MouseDpiState ParseCurrent(byte[] frame) {
            if (!Matches(frame, 4, 0x85, 5)) return null;
            int x = BigWord(frame, 10), y = BigWord(frame, 12);
            return x == 0 || y == 0 ? null : new MouseDpiState {
                Mode = 1, CanSwitch = false, Current = x, CurrentVertical = y
            };
        }

        internal static MouseDpiState ReadDpi(HidLink link) {
            return ParseStages(Query(link, 4, 0x86, 0x26)) ??
                ParseStages(Query(link, 4, 0x86, 0x26, 1)) ??
                ParseCurrent(Query(link, 4, 0x85, 7, 1)) ??
                ParseCurrent(Query(link, 4, 0x85, 7));
        }
    }
}
