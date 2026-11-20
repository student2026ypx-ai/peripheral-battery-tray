using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    internal static class HyperXHeadsetBattery {
        const byte LevelCommand = 2, ChargeCommand = 3, Cloud3SLevel = 6, Cloud3SCharge = 0x48;

        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return Cloud2(0x0696);
            yield return Cloud2(0x018B);
            yield return new UsbBatteryDevice(0x0951,0x1718,"headset","HyperX Cloud II Wireless",true,
                info => ExpandedUsbBattery.Page(info,0xFF13) && info.Usage == 1 &&
                    info.InputLength >= 8 && info.OutputLength == 62,link => ReadCloud2(link,true));
            yield return Cloud3S(0x02CC);
            yield return Cloud3S(0x06BE);
            yield return new UsbBatteryDevice(0x03F0,0x08BE,"headset","HyperX Cloud Alpha 2",true,
                info => ExpandedUsbBattery.Page(info,0xFF13) && info.Usage == 0xFF00 &&
                    info.InputLength == 64 && info.OutputLength == 64,ReadAlpha2);
        }

        static UsbBatteryDevice Cloud2(ushort product) {
            return new UsbBatteryDevice(0x03F0,product,"headset","HyperX Cloud II Wireless",true,
                info => ExpandedUsbBattery.Page(info,0xFF90) && info.Usage == 0x0303 &&
                    info.InputLength >= 20 && info.OutputLength >= 52,link => ReadCloud2(link,false));
        }

        static UsbBatteryDevice Cloud3S(ushort product) {
            var model = new UsbBatteryDevice(0x03F0,product,"headset","HyperX Cloud III S Wireless",true,
                info => info.OutputLength == 64,
                null);
            model.ReadGroup = ReadCloud3S;
            return model;
        }

        internal static int? ParseCloud2(byte[] frame, byte command, bool kingston) {
            if (frame == null || frame.Length < 8 || frame[0] != (kingston ? 0x0B : 6) ||
                (!kingston && frame[1] != 0xFF) || frame[2] != 0xBB || frame[3] != command) return null;
            int value = frame[command == LevelCommand ? 7 : 4];
            return value <= (command == LevelCommand ? 100 : 2) ? (int?)value : null;
        }

        static int? QueryCloud2(HidLink link, byte command, bool kingston) {
            if (kingston) link.ReadInputReport(6);
            link.Write(kingston
                ? new byte[]{6,0,2,0,0x9A,0,0,0x68,0x4A,0x8E,0x0A,0,0,0,0xBB,command,0}
                : new byte[]{6,0xFF,0xBB,command,0});
            if (kingston) Thread.Sleep(100);
            int? value = null;
            link.ReadReply(frame => (value = ParseCloud2(frame,command,kingston)).HasValue,1000);
            return value;
        }

        static BatteryState ReadCloud2(HidLink link, bool kingston) {
            if (!HidNative.HasReport(link.Handle,1,6,link.Info.OutputLength) ||
                !HidNative.HasReport(link.Handle,0,(byte)(kingston ? 0x0B : 6),link.Info.InputLength)) return null;
            int? level = QueryCloud2(link,LevelCommand,kingston);
            if (!level.HasValue) return null;
            int? charging = QueryCloud2(link,ChargeCommand,kingston);
            return ExpandedUsbBattery.Level(level.Value,charging.HasValue ? (bool?)(charging.Value == 1) : null);
        }

        internal static BatteryState ParseAlpha2(byte[] frame) {
            return frame != null && frame.Length >= 7 && frame[0] == 0x51 && frame[1] == 2 && frame[2] <= 100
                ? ExpandedUsbBattery.Level(frame[2],(frame[6]&0x80) != 0) : null;
        }

        static BatteryState ReadAlpha2(HidLink link) {
            for (int i=0;i<64 && link.Read(3) != null;i++) {}
            return ExpandedUsbBattery.Ask(link,new byte[]{0x50,2},ParseAlpha2,1500,0x51);
        }

        internal static int? ParseCloud3S(byte[] frame, byte command) {
            if (frame == null || frame.Length < 7) return null;
            int value;
            if (frame[0] == 0x0C && frame[5] == command) value = frame[6];
            else if (frame[0] == 0x0D && frame[4] == (command == Cloud3SLevel ? 1 : 10)) value = frame[5];
            else return null;
            return value <= (command == Cloud3SLevel ? 100 : 2) ? (int?)value : null;
        }

        static bool Cloud3SInput(HidLink link) {
            return link.Info.InputLength >= 7 && (HidNative.HasReport(link.Handle,0,0x0C,link.Info.InputLength) ||
                HidNative.HasReport(link.Handle,0,0x0D,link.Info.InputLength));
        }

        static int? QueryCloud3S(HidLink writer, IList<HidLink> readers, byte command) {
            writer.Write(new byte[]{0x0C,2,3,1,0,command});
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1000) {
                foreach (var reader in readers) {
                    try {
                        var value = ParseCloud3S(reader.Read(25),command);
                        if (value.HasValue) return value;
                    } catch (Win32Exception) {}
                }
            }
            return null;
        }

        static BatteryState ReadCloud3S(HidLink link, IEnumerable<HidInfo> channels) {
            if (!HidNative.HasReport(link.Handle,1,0x0C,64)) return null;
            var readers = new List<HidLink>();
            if (Cloud3SInput(link)) readers.Add(link);
            try {
                foreach (var info in channels.Where(item => item.Transport == "usb" &&
                        item.Vid == link.Info.Vid && item.Pid == link.Info.Pid && item.Container == link.Info.Container &&
                        item.InputLength >= 7 && item.Path != link.Info.Path)) {
                    try {
                        var reader = new HidLink(info,true);
                        if (Cloud3SInput(reader)) readers.Add(reader);
                        else reader.Dispose();
                    } catch (Win32Exception) {}
                }
                if (readers.Count == 0) return null;
                int? level = QueryCloud3S(link,readers,Cloud3SLevel);
                if (!level.HasValue) return null;
                int? charge = QueryCloud3S(link,readers,Cloud3SCharge);
                return ExpandedUsbBattery.Level(level.Value,charge.HasValue ? (bool?)(charge.Value == 1) : null);
            } finally {
                foreach (var reader in readers) if (reader != link) reader.Dispose();
            }
        }
    }
}
