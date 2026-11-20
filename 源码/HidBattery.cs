using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PeripheralBattery {
    internal static class HidBattery {
        const int Feature = 2;
        const int Success = 0x00110000;
        const ushort DeviceControls = 0x06, BatteryStrength = 0x20;

        // The Windows SDK HIDP_VALUE_CAPS structure has the same layout on x64.
        [StructLayout(LayoutKind.Explicit, Size = 72)]
        internal struct ValueCaps {
            [FieldOffset(2)] public byte ReportId;
            [FieldOffset(6)] public ushort LinkCollection;
            [FieldOffset(15)] public byte IsAbsolute;
            [FieldOffset(18)] public ushort BitSize;
            [FieldOffset(20)] public ushort ReportCount;
            [FieldOffset(40)] public int Minimum;
            [FieldOffset(44)] public int Maximum;
        }

        [DllImport("hid.dll")]
        static extern int HidP_GetSpecificValueCaps(int type, ushort page, ushort link, ushort usage,
            [Out] ValueCaps[] caps, ref ushort count, IntPtr data);

        [DllImport("hid.dll")]
        static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage,
            out uint value, IntPtr data, byte[] report, uint length);

        internal static ValueCaps[] FindFields(IntPtr data, ushort count) {
            if (count == 0) return null;
            var values = new ValueCaps[count];
            if (HidP_GetSpecificValueCaps(Feature, DeviceControls, 0, BatteryStrength, values, ref count, data) != Success)
                return null;
            var fields = new List<ValueCaps>();
            for (int i = 0; i < count; i++) {
                var field = values[i];
                if (field.IsAbsolute != 0 && field.ReportCount == 1 && field.BitSize > 0 && field.BitSize <= 32 &&
                    field.Minimum < field.Maximum) fields.Add(field);
            }
            return fields.Count > 0 ? fields.ToArray() : null;
        }

        // Battery Strength reports remaining charge over the descriptor's logical range.
        // An out-of-range/null value denotes unavailable telemetry.
        internal static int? ToPercent(uint raw, ValueCaps field) {
            if (field.BitSize == 0 || field.BitSize > 32 || field.Minimum >= field.Maximum) return null;
            long range = 1L << field.BitSize;
            long value = raw;
            if (value >= range) return null;
            if (field.Minimum < 0 && value >= range / 2) value -= range;
            if (value < field.Minimum || value > field.Maximum) return null;
            long span = (long)field.Maximum - field.Minimum;
            return (int)(((value - field.Minimum) * 100 + span / 2) / span);
        }

        static int? ReadField(HidInfo info) {
            if (info.BatteryFields == null || info.FeatureLength < 2) return null;
            using (var handle = HidNative.CreateFile(info.Path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                IntPtr data;
                if (handle.IsInvalid || !HidNative.HidD_GetPreparsedData(handle, out data)) return null;
                try {
                    foreach (var field in info.BatteryFields) {
                        var report = new byte[info.FeatureLength];
                        report[0] = field.ReportId;
                        uint raw;
                        if (!HidNative.HidD_GetFeature(handle, report, report.Length) || report[0] != field.ReportId ||
                            HidP_GetUsageValue(Feature, DeviceControls, field.LinkCollection, BatteryStrength,
                                out raw, data, report, (uint)report.Length) != Success) continue;
                        int? percent = ToPercent(raw, field);
                        if (percent.HasValue) return percent;
                    }
                    return null;
                } finally {
                    HidNative.HidD_FreePreparsedData(data);
                }
            }
        }

        internal static int? Read(IEnumerable<uint> nodes, IEnumerable<HidInfo> channels) {
            var reported = WindowsDevices.ReadBatteryPercent(nodes);
            if (reported.HasValue) return reported;
            foreach (var channel in channels) {
                var percent = ReadField(channel);
                if (percent.HasValue) return percent;
            }
            return null;
        }
    }
}
