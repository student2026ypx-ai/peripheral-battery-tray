using System;
using System.Collections.Generic;

namespace PeripheralBattery {
    internal static class HeadsetReceiverBattery {
        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return Atk(0x1262,"ATK N9 V2 Ultimate");
            yield return Atk(0x12D6,"ATK N9 V2 Ultra");
            yield return MchoseTurbo(0x600D,"MCHOSE V9 Turbo",true);
            yield return MchoseTurbo(0x600A,"MCHOSE V9 Turbo+",true);
            yield return MchoseTurbo(0x6009,"MCHOSE V9 Turbo+",false);
            yield return new UsbBatteryDevice(0x3837,0x6011,"headset","MCHOSE V9 Pro 2",true,
                MchoseChannel,link => ExpandedUsbBattery.Ask(link,
                    new byte[]{0x55,0xAA,1,0,1,0,1,0x0B,0},ParseMchose,1000,0x55));
            foreach (var model in HyperXHeadsetBattery.Devices()) yield return model;
            foreach (var model in CorsairHeadsetBattery.Devices()) yield return model;
            foreach (var model in RazerHeadsetBattery.Devices()) yield return model;
            foreach (var model in LogitechHeadsetBattery.Devices()) yield return model;
        }

        static UsbBatteryDevice Atk(ushort product, string name) {
            return new UsbBatteryDevice(0x373B,product,"headset",name,true,
                info => ExpandedUsbBattery.Page(info,0xFF90) && info.Usage == 1 &&
                    info.InputLength >= 22 && info.InputLength <= 64 && info.OutputLength == 64,
                link => ExpandedUsbBattery.Ask(link,new byte[]{0x55,0x65,1},ParseAtk,1000,0x55));
        }

        internal static BatteryState ParseAtk(byte[] frame) {
            if (frame == null || frame.Length < 22 || frame.Length > 64 || frame[0] != 0x55 ||
                frame[1] != 0x65 || frame[2] > 100 || frame[3] < 2 || frame[3] > 4) return null;
            return ExpandedUsbBattery.Level(frame[2],frame[3] == 3);
        }

        static bool MchoseChannel(HidInfo info) {
            return ExpandedUsbBattery.Page(info,0xFF07) && info.Usage == 0x0222 &&
                ExpandedUsbBattery.OnInterface(info,3) && info.InputLength == 64 && info.OutputLength == 64 &&
                String.Equals(info.Product,"MCHOSE V9 Pro 2",StringComparison.OrdinalIgnoreCase);
        }

        static UsbBatteryDevice MchoseTurbo(ushort product, string name, bool wireless) {
            return new UsbBatteryDevice(0x3837,product,"headset",name,wireless,
                info => info.InputLength >= 4 && (info.OutputLength == 16 || info.OutputLength == 64),
                link => ExpandedUsbBattery.Ask(link,new byte[]{0x55,0x65,1},UsbHeadsetSupport.ParseMchose,1000,0x55));
        }

        internal static BatteryState ParseMchose(byte[] frame) {
            if (frame == null || frame.Length != 64 || frame[0] != 0x55 || frame[1] != 0xAA ||
                frame[2] != 1 || frame[3] > 1 || frame[4] < 3 || frame[5] != 0 || frame[6] != 1 ||
                frame[7] != 0x0B || frame[8] == 0 || (frame[9]&0x7F) > 100) return null;
            int checksumIndex = 7+frame[4];
            if (checksumIndex >= frame.Length) return null;
            byte checksum = 0;
            if (frame[3] == 1) {
                for (int i=3;i<checksumIndex;i++) checksum ^= frame[i];
            }
            return frame[checksumIndex] == checksum
                ? ExpandedUsbBattery.Level(frame[9]&0x7F,(frame[9]&0x80) != 0) : null;
        }
    }
}
