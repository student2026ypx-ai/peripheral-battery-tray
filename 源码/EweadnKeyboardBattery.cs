using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    internal static class EweadnKeyboardBattery {
        // E HUB 2.0 uses 64-byte unnumbered feature reports. Windows prefixes a zero report ID.
        const int ReportLength = 65;
        const byte Status = 0xF7, SelectTarget = 0xF6, Identify = 0x8F, ReleaseReply = 0xFC;
        const int ReplyReadyOffset = 1, BatteryOffset = 2, KeyboardOfflineOffset = 4;
        const int SendReadyOffset = 6, DeviceKindOffset = 7, RadioBootOffset = 9;
        const ushort Vendor = 0x3151, X87sReceiver = 0x504E;

        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return Receiver(X87sReceiver);
            yield return Receiver(0x5026);
            yield return Receiver(0x502B);
        }

        static UsbBatteryDevice Receiver(ushort product) {
            return new UsbBatteryDevice(Vendor, product, "keyboard", null, true, IsChannel, null) {
                ReadGroup = (link, devices) => devices.Count(info => info.Vid == Vendor && info.Pid == product && IsChannel(info)) == 1
                    ? ReadPower(product, link.WriteFeature, link.ReadFeature) : null
            };
        }

        static bool IsChannel(HidInfo info) {
            return info.Transport == "usb" && info.UsagePage == 0xFFFF && info.Usage == 2 &&
                info.FeatureLength == ReportLength &&
                string.Equals(info.Product, "2.4G Wireless Keyboard", StringComparison.OrdinalIgnoreCase);
        }

        internal static BatteryState ReadPower(ushort product, Func<byte[], bool> writeFeature, Func<byte, byte[]> readFeature) {
            if (WaitForStatus(writeFeature, readFeature, SendReadyOffset) == null ||
                !writeFeature(Request(SelectTarget, 0x0A))) return null;
            Thread.Sleep(100);
            if (!writeFeature(Request(Identify))) return null;
            Thread.Sleep(100);
            var status = WaitForStatus(writeFeature, readFeature, ReplyReadyOffset);
            if (status == null || !writeFeature(Request(ReleaseReply))) return null;
            Thread.Sleep(100);

            string name = IdentifyKeyboard(product, readFeature(0));
            if (name == null) return null;
            var reading = ExpandedUsbBattery.Level(status[BatteryOffset]);
            reading.Name = name;
            return reading;
        }

        static byte[] WaitForStatus(Func<byte[], bool> writeFeature, Func<byte, byte[]> readFeature, int flagOffset) {
            var request = Request(Status);
            for (int attempt = 0; attempt < 5; attempt++) {
                if (!writeFeature(request)) return null;
                Thread.Sleep(15);
                var status = readFeature(0);
                if (!IsOnlineStatus(status)) return null;
                if (status[flagOffset] == 1) return status;
                Thread.Sleep(85);
            }
            return null;
        }

        static bool IsOnlineStatus(byte[] status) {
            return IsFeatureReport(status) && status[ReplyReadyOffset] <= 1 && status[SendReadyOffset] <= 1 &&
                (status[DeviceKindOffset] == 1 || status[DeviceKindOffset] == 3) &&
                status[KeyboardOfflineOffset] == 0 && status[RadioBootOffset] == 0 && status[BatteryOffset] <= 100;
        }

        static byte[] Request(byte command, byte argument = 0) {
            var report = new byte[ReportLength];
            report[1] = command;
            report[2] = argument;
            if (command == Identify) report[8] = 0x70;
            return report;
        }

        static bool IsFeatureReport(byte[] report) {
            return report != null && report.Length == ReportLength && report[0] == 0;
        }

        static string IdentifyKeyboard(ushort product, byte[] report) {
            if (!IsFeatureReport(report) || report[1] != Identify) return null;
            uint id = BitConverter.ToUInt32(report, 2);
            // Board identities from the receiver entries in the official E HUB 2.0 manifest.
            if (product == X87sReceiver) return id == 0xCD0 ? "前行者 X87S" : null;
            if (product != 0x5026 && product != 0x502B) return null;
            switch (id) {
                case 0xA97:
                case 0xA12:
                case 0xA96: return "前行者 DEEP68";
                case 0xA0E:
                case 0xA5C: return "前行者 DEEP80";
                case 0xB5A: return "前行者 DEEP80 Pro";
                case 0x97A: return "前行者 ZAP68";
                case 0x9CE: return "前行者 ZAP68 Ultra";
                case 0x9DF: return "前行者 ZAP87";
                default: return null;
            }
        }
    }
}
