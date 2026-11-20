using System;
using System.Diagnostics;

namespace PeripheralBattery {
    // Jm command collection used by the A950 PRO Mg and its TM265 receiver.
    internal static class DareuMouse {
        const int Length = 17;
        const byte ReportId = 8;

        internal static byte[] BuildRequest(byte command, int address = 0, byte count = 0) {
            var bytes = new byte[Length];
            bytes[0] = ReportId;
            bytes[1] = command;
            bytes[3] = (byte)(address >> 8);
            bytes[4] = (byte)address;
            bytes[5] = count;
            bytes[Length - 1] = Checksum(bytes, 0, Length - 1);
            return bytes;
        }

        static byte Checksum(byte[] data, int offset, int count) {
            int sum = 0;
            for (int i = offset; i < offset + count; i++) sum += data[i];
            return (byte)(((sum < 55 ? 85 : 341) - sum) & 255);
        }

        internal static bool Matches(byte[] frame, byte[] request) {
            if (frame == null || frame.Length != Length || frame[0] != ReportId || frame[1] != request[1])
                return false;
            return request[1] != 8 || (frame[3] == request[3] && frame[4] == request[4] && frame[5] == request[5]);
        }

        static byte[] Query(HidLink link, byte[] request) {
            link.Write(request);
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 500) {
                var frame = link.Read(70);
                if (Matches(frame, request)) return frame;
            }
            return null;
        }

        static bool Linked(HidLink link) {
            if (!HidNative.HasReport(link.Handle, 0, ReportId, Length) ||
                !HidNative.HasReport(link.Handle, 1, ReportId, Length)) return false;
            var frame = Query(link, BuildRequest(3));
            return frame != null && frame[6] == 1;
        }

        internal static UsbMousePower ParsePower(byte[] frame) {
            if (frame == null || frame.Length != Length || frame[0] != ReportId || frame[1] != 4 || frame[6] > 100)
                return null;
            return new UsbMousePower { Percent = frame[6], Charging = frame[7] <= 2 ? (bool?)(frame[7] != 0) : null };
        }

        internal static UsbMousePower ReadPower(HidLink link) {
            return Linked(link) ? ParsePower(Query(link, BuildRequest(4))) : null;
        }

        static byte[] ReadBuffer(HidLink link, int address, int length) {
            var data = new byte[length];
            for (int offset = 0; offset < length; offset += 10) {
                int count = Math.Min(10, length - offset);
                var frame = Query(link, BuildRequest(8, address + offset, (byte)count));
                if (frame == null) return null;
                Buffer.BlockCopy(frame, 6, data, offset, count);
            }
            return data;
        }

        static int DecodeDpi(int low, int high) {
            int index = high == 0 ? low : high == 0x44 ? low + 256 : high == 0x88 ? low + 512 : -1;
            int dpi = (index + 1) * 50;
            return dpi >= 100 && dpi <= 26000 ? dpi : 0;
        }

        static bool ValidHeader(byte[] header) {
            return header != null && header.Length == 4 && header[0] >= 1 && header[0] <= 5 &&
                header[2] < header[0] && header[1] == Checksum(header, 0, 1) && header[3] == Checksum(header, 2, 1);
        }

        internal static MouseDpiState ParseDpi(byte[] header, byte[] records, int profile) {
            if (!ValidHeader(header) || records == null || records.Length != header[0] * 4 || profile < 0 || profile >= 4)
                return null;
            var x = new int[header[0]];
            var y = new int[header[0]];
            for (int i = 0; i < x.Length; i++) {
                int offset = i * 4;
                if (records[offset + 3] != Checksum(records, offset, 3)) return null;
                x[i] = DecodeDpi(records[offset], records[offset + 2]);
                y[i] = DecodeDpi(records[offset + 1], records[offset + 2]);
                if (x[i] == 0 || y[i] == 0) return null;
            }
            int active = header[2];
            return new MouseDpiState { Mode = 1, CanSwitch = false, Index = active, Profile = (ushort)(profile + 1),
                Current = x[active], Presets = x, VerticalPresets = y };
        }

        internal static MouseDpiState ReadDpi(HidLink link) {
            if (!Linked(link)) return null;
            var profileRequest = BuildRequest(14);
            var before = Query(link, profileRequest);
            if (before == null || before[6] >= 4) return null;
            var header = ReadBuffer(link, 2, 4);
            if (!ValidHeader(header)) return null;
            var records = ReadBuffer(link, 12, header[0] * 4);
            if (records == null) return null;
            var after = Query(link, profileRequest);
            return after == null || after[6] != before[6] ? null : ParseDpi(header, records, before[6]);
        }
    }
}
