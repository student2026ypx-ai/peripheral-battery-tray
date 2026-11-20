using System;

namespace PeripheralBattery {
    // SC380 Pro's model signature is checked before battery or settings reads.
    internal static class AulaMouse {
        const int FeatureSize = 520, HeaderSize = 8, SettingsSize = 400;
        const byte ReportId = 9;

        internal static byte[] BuildRequest(byte command, byte argument, int payloadSize) {
            var frame = new byte[FeatureSize];
            frame[0] = ReportId;
            frame[2] = command;
            frame[3] = argument;
            frame[4] = 1;
            frame[6] = (byte)payloadSize;
            frame[7] = (byte)(payloadSize >> 8);
            frame[1] = Sum(frame, 2, HeaderSize);
            return frame;
        }

        static byte Sum(byte[] bytes, int start, int end) {
            int sum = 0;
            for (int i = start; i < end; i++) sum += bytes[i];
            return (byte)(sum & 255);
        }

        internal static byte[] Decode(byte[] frame, byte command, byte argument, int length) {
            if (frame == null || frame.Length != FeatureSize || length < 1 || length > FeatureSize - HeaderSize ||
                frame[0] != ReportId || frame[2] != command || frame[3] != argument ||
                frame[4] != 1 || frame[5] != 0 || UsbMouseSupport.LittleWord(frame, 6) != length) return null;
            int end = command == 0x44 ? HeaderSize + length : HeaderSize;
            if (frame[1] != Sum(frame, 2, end)) return null;
            var data = new byte[length];
            Buffer.BlockCopy(frame, HeaderSize, data, 0, length);
            return data;
        }

        static byte[] Query(HidLink link, byte command, byte argument, int length) {
            var reply = UsbMouseSupport.RequestFeature(link, BuildRequest(command, argument, length), 35);
            return Decode(reply, command, argument, length);
        }

        internal static bool IsSc380Pro(byte[] signature) {
            return signature != null && signature.Length == 6 && signature[0] == 4 && signature[1] == 0 &&
                signature[2] == 0 && signature[3] == 0 && signature[4] == 0 && signature[5] == 0x2E;
        }

        static bool Linked(HidLink link) {
            var reply = Query(link, 7, 0, 1);
            return reply != null && reply[0] == 1;
        }

        internal static UsbMousePower ParsePower(byte[] data) {
            return data != null && data.Length == 1 && data[0] <= 100 ? new UsbMousePower { Percent = data[0] } : null;
        }

        internal static UsbMousePower ReadPower(HidLink link) {
            if (!IsSc380Pro(Query(link, 5, 1, 6))) return null;
            var power = Linked(link) ? ParsePower(Query(link, 9, 1, 1)) : null;
            return power ?? new UsbMousePower();
        }

        internal static MouseDpiState ParseDpi(byte[] block) {
            if (block == null || block.Length != SettingsSize || block[0] != 0x64 || (block[1] & 31) != 0x18) return null;
            int count = block[3] & 15, active = (block[3] >> 4) - 1, rate = block[2] & 15;
            if (count < 1 || count > 6 || active < 0 || active >= count || rate < 1 || rate > 4) return null;
            var horizontal = new int[count];
            var vertical = new int[count];
            for (int i = 0; i < count; i++) {
                horizontal[i] = (UsbMouseSupport.LittleWord(block, 7 + 2 * i) + 1) * 100;
                vertical[i] = (UsbMouseSupport.LittleWord(block, 27 + 2 * i) + 1) * 100;
                if (horizontal[i] > 12000 || vertical[i] > 12000) return null;
            }
            return new MouseDpiState { Mode = 1, CanSwitch = false, Index = active,
                Current = horizontal[active], Presets = horizontal, VerticalPresets = vertical };
        }

        internal static MouseDpiState ReadDpi(HidLink link) {
            return IsSc380Pro(Query(link, 5, 1, 6)) && Linked(link)
                ? ParseDpi(Query(link, 0x44, 0, SettingsSize)) : null;
        }
    }
}
