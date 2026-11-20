using System;
using System.Collections.Generic;
using Reply = PeripheralBattery.ExpandedHeadsetBattery.SteelSeriesReply;

namespace PeripheralBattery {
    internal static class SteelSeriesHeadsetBattery {
        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return ExpandedHeadsetBattery.Steel(0x2258,"Arctis Nova 7X",Reply.NovaPercent);
            yield return ExpandedHeadsetBattery.Steel(0x229E,"Arctis Nova 7X",Reply.NovaPercent);
            yield return ExpandedHeadsetBattery.Steel(0x22AD,"Arctis Nova 7X",Reply.NovaPercent);
            yield return ExpandedHeadsetBattery.Steel(0x22A4,"Arctis Nova 7X",Reply.NovaSteps);
            yield return ExpandedHeadsetBattery.Steel(0x223A,"Arctis Nova 7 Diablo IV",Reply.NovaSteps);
            yield return ExpandedHeadsetBattery.Steel(0x22A9,"Arctis Nova 7 Diablo IV",Reply.NovaPercent);
            yield return ExpandedHeadsetBattery.Steel(0x227A,"Arctis Nova 7 WoW Edition",Reply.NovaSteps);
            yield return ExpandedHeadsetBattery.Steel(0x2298,"Arctis Nova 7P",Reply.NovaPercent);
            yield return ExpandedHeadsetBattery.Steel(0x226D,"Arctis Nova 3X Wireless",Reply.Nova5);
            yield return ExpandedHeadsetBattery.Steel(0x220E,"Arctis 7+",Reply.Arctis7Plus);
            yield return ExpandedHeadsetBattery.Steel(0x2212,"Arctis 7P+",Reply.Arctis7Plus);
            yield return ExpandedHeadsetBattery.Steel(0x2216,"Arctis 7X+",Reply.Arctis7Plus);
            yield return ExpandedHeadsetBattery.Steel(0x2236,"Arctis 7+ Destiny",Reply.Arctis7Plus);

            yield return Arctis1(0x12B3,"Arctis 1 Wireless");
            yield return Arctis1(0x12B6,"Arctis 1 Wireless for Xbox");
            yield return Arctis1(0x12D5,"Arctis 7P");
            yield return Arctis1(0x12D7,"Arctis 7X");
            yield return Legacy(0x1260,"Arctis 7",5,link => ReadArctis7(link,false));
            yield return Legacy(0x12AD,"Arctis 7 (2019)",5,link => ReadArctis7(link,true));
            yield return Legacy(0x1252,"Arctis Pro Wireless (2019)",5,link => ReadArctis7(link,false));
            yield return Legacy(0x12C2,"Arctis 9",0,
                link => ExpandedUsbBattery.Ask(link,new byte[]{0,0x20},ParseArctis9,800));
            yield return NovaPro(0x12E0,"Arctis Nova Pro Wireless");
            yield return NovaPro(0x12E5,"Arctis Nova Pro Wireless X");
            yield return new UsbBatteryDevice(0x1038,0x2244,"headset","SteelSeries Arctis Nova Elite",true,
                info => ExpandedUsbBattery.Page(info,0xFFC0) && ExpandedUsbBattery.OnInterface(info,3) &&
                    info.Usage == 1 && info.InputLength >= 16 && info.OutputLength == 64,
                link => ExpandedUsbBattery.Ask(link,new byte[]{1,0xB0},ParseNovaElite,1000,1));
        }

        static UsbBatteryDevice Legacy(ushort product, string name, int usbInterface, Func<HidLink,BatteryState> read) {
            return new UsbBatteryDevice(0x1038,product,"headset","SteelSeries "+name,true,
                info => ExpandedUsbBattery.VendorPage(info) && ExpandedUsbBattery.OnInterface(info,usbInterface) &&
                    info.InputLength >= 3 && info.OutputLength >= 2,read);
        }

        static UsbBatteryDevice Arctis1(ushort product, string name) {
            return new UsbBatteryDevice(0x1038,product,"headset","SteelSeries "+name,true,
                info => ExpandedUsbBattery.Page(info,0xFF43) && ExpandedUsbBattery.OnInterface(info,3) &&
                    info.InputLength >= 4 && info.OutputLength >= 2,
                link => ExpandedUsbBattery.Ask(link,new byte[]{6,0x12},ParseArctis1,800,6));
        }

        internal static BatteryState ParseArctis1(byte[] frame) {
            return frame != null && frame.Length >= 4 && frame[0] == 6 && frame[1] == 0x12 &&
                frame[2] != 1 && frame[3] <= 100 && (frame[2] != 0 || frame[3] != 0)
                ? ExpandedUsbBattery.Level(frame[3]) : null;
        }

        internal static BatteryState ParseArctis7(byte[] frame, bool onlineConfirmed) {
            return frame != null && frame.Length >= 3 && frame[0] == 6 && frame[1] == 0x18 &&
                (onlineConfirmed || frame[2] != 0) && frame[2] <= 100
                ? ExpandedUsbBattery.Level(frame[2]) : null;
        }

        static BatteryState ReadArctis7(HidLink link, bool checkLink) {
            if (!HidNative.HasReport(link.Handle,1,6,link.Info.OutputLength) ||
                !HidNative.HasReport(link.Handle,0,6,link.Info.InputLength)) return null;
            if (checkLink) {
                link.Write(new byte[]{6,0x14});
                var status = link.ReadReply(frame => frame.Length >= 3 && frame[0] == 6 && frame[1] == 0x14,600);
                if (status == null || status[2] != 3) return null;
            }
            return ExpandedUsbBattery.Ask(link,new byte[]{6,0x18},frame => ParseArctis7(frame,checkLink),800,6);
        }

        internal static BatteryState ParseArctis9(byte[] frame) {
            if (frame == null || frame.Length < 5) return null;
            int offset = frame[0] == 0 ? 1 : 0;
            if (frame.Length < offset+5 || frame[offset] != 0xAA || frame[offset+1] != 1 ||
                frame[offset+3] < 0x64 || frame[offset+3] > 0x9A || frame[offset+4] > 1) return null;
            return ExpandedUsbBattery.Level((frame[offset+3]-0x64)*100/54,frame[offset+4] == 1,true);
        }

        static UsbBatteryDevice NovaPro(ushort product, string name) {
            return new UsbBatteryDevice(0x1038,product,"headset","SteelSeries "+name,true,
                info => ExpandedUsbBattery.VendorPage(info) && info.InputLength >= 16 && info.OutputLength >= 2 &&
                    (ExpandedUsbBattery.OnInterface(info,3) || ExpandedUsbBattery.OnInterface(info,4)),
                link => ExpandedUsbBattery.Ask(link,new byte[]{6,0xB0},ParseNovaPro,1000));
        }

        internal static BatteryState ParseNovaPro(byte[] frame) {
            if (frame == null || frame.Length < 16) return null;
            int offset = frame[0] == 0 ? 1 : 0;
            if (frame.Length < offset+16 || frame[offset+6] > 8 ||
                (frame[offset+15] != 2 && frame[offset+15] != 8)) return null;
            return ExpandedUsbBattery.Level(frame[offset+6]*100/8,frame[offset+15] == 2,true);
        }

        internal static BatteryState ParseNovaElite(byte[] frame) {
            if (frame == null || frame.Length < 16 || frame[0] != 1 || frame[1] != 0xB0 ||
                frame[6] > 100 || frame[14] == 1 ||
                (frame[14] == 0 && frame[15] == 0 && frame[6] == 0)) return null;
            return ExpandedUsbBattery.Level(frame[6],frame[14] == 2 || frame[15] == 2);
        }
    }
}
