using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery {
    internal static class MouseReceiverBattery {
        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return UniqueDevice(0x089D,0x062F,"前行者 S9",
                info => info.UsagePage == 0xFF01 && info.Usage == 0x10 && info.OutputLength == 33 &&
                    info.InputLength >= 16 && info.InputLength <= 34,
                link => ExpandedUsbBattery.Ask(link,new byte[]{0,0x30},ParseS9,950,0),
                readDpi:EweadnMouseDpi.ReadS9);
            yield return UniqueDevice(0x372E,0x1026,"前行者 S9 PRO 8K",
                info => (info.UsagePage == 0xFF00 || info.UsagePage == 0xFF06) &&
                    info.Usage == 1 && info.FeatureLength == 64,ReadS9Pro,readDpi:EweadnMouseDpi.ReadS9Pro);
            yield return UniqueDevice(0x04F3,0x026F,"前行者 X23 Pro",
                info => info.UsagePage == 0x000A && info.Usage == 0 && info.InputLength == 32 &&
                    ProductIs(info,"X23 Pro Receiver") && OnCollection(info,1,4),
                ReadX23,true,readDpi:EweadnMouseDpi.ReadX23Stage);

            yield return UniqueDevice(0x3837,0x4245,"MCHOSE G3 V2",
                info => Radio65(info) && ProductIs(info,"MCHOSE G3 V2"),ReadMchoseG3,
                readDpi:MouseDpiQueries.ReadMchose);
            yield return UniqueDevice(0x391D,0x1A01,"VGN Dragonfly 3 SE",Radio65,
                link => ExpandedUsbBattery.Ask(link,new byte[]{0,0x55,0x30},ParseDragonfly3Se,950,0),
                readDpi:VgnMouseDpi.ReadSe);
            yield return VgnRyMouse(0x1A04,"VGN Dragonfly 3 Pro Max");
            yield return VgnRyMouse(0x1A08,"VGN Dragonfly Y2 V2 Pro Max");
            yield return VgnRyMouse(0x1A13,"VGN Dragonfly F1 V2 Pro Max");
            yield return VgnRyMouse(0x1A25,"VGN Dragonfly 3 Ultra");
            yield return VgnRyMouse(0x1A2B,"VGN Dragonfly F1 V5 Ultra");
            yield return VgnRyMouse(0x1A41,"VGN Dragonfly F2 V2 Ultra");

            yield return Rapoo(0x1411,"雷柏 VT3S");
            yield return Rapoo(0x1412,"雷柏 VT3");
            yield return Rapoo(0x1421,"雷柏 VT3S MAX");
            yield return Rapoo(0x14A1,"雷柏 VT0 Air MAX");
            yield return Rapoo(0x4611,"雷柏 VT3S",false);
            yield return Rapoo(0x1464,"雷柏 VT3S V2");
            yield return Rapoo(0x4664,"雷柏 VT3S V2",false);
            yield return Rapoo(0x14A9,"雷柏 VT9 PRO V2",true,0x0E);
            yield return Rapoo(0x46A9,"雷柏 VT9 PRO V2",false);
            yield return Rapoo(0x1420,"雷柏 VT0 MAX");
            yield return Rapoo(0x4620,"雷柏 VT0 MAX",false);
            yield return Rapoo(0x144A,"雷柏 VT0 MAX 大师版");
            yield return Rapoo(0x464A,"雷柏 VT0 MAX 大师版",false);
            yield return Rapoo(0x1417,"雷柏 VT3 MAX");
            yield return Rapoo(0x4617,"雷柏 VT3 MAX",false);
            yield return Rapoo(0x1B06,"雷柏 VT3 MAX V2");
            yield return Rapoo(0x4B06,"雷柏 VT3 MAX V2",false);
            yield return Rapoo(0x146D,"雷柏 VT3 MAX 大师版 V2");
            yield return Rapoo(0x466D,"雷柏 VT3 MAX 大师版 V2",false);
            yield return Rapoo(0x144B,"雷柏 VT3S MAX 大师版");
            yield return Rapoo(0x464B,"雷柏 VT3S MAX 大师版",false);
            yield return Rapoo(0x146E,"雷柏 VT3S MAX 大师版 V2");
            yield return Rapoo(0x466E,"雷柏 VT3S MAX 大师版 V2",false);

            foreach (var mouse in RogPair(0x1A92,0x1A94,"ROG Harpe Ace Aim Lab Edition",minimumDpi:50)) yield return mouse;
            foreach (var mouse in RogPair(0x1A66,0x1A68,"ROG Keris Wireless AimPoint")) yield return mouse;
            foreach (var mouse in RogPair(0x197D,0x197F,"ROG Gladius III Wireless",separateAxes:false,maximumDpi:26000)) yield return mouse;
            foreach (var mouse in RogPair(0x1B0A,0x1B0C,"ROG Gladius III Wireless AimPoint EVA-02")) yield return mouse;
            foreach (var mouse in RogPair(0x1977,0x1979,"ROG Spatha X",separateAxes:false,maximumDpi:19000)) yield return mouse;
        }

        internal static UsbBatteryDevice UniqueDevice(ushort vendor, ushort product, string name,
                Func<HidInfo,bool> channel, Func<HidLink,BatteryState> read, bool listenOnly = false, bool wireless = true,
                Func<HidLink,MouseDpiState> readDpi = null) {
            var model = new UsbBatteryDevice(vendor,product,"mouse",name,wireless,channel,read,listenOnly);
            model.ReadGroup = (link,group) => group.Count(model.Matches) == 1 ? read(link) : null;
            model.ReadMouseDpi = readDpi;
            return model;
        }

        static bool Radio65(HidInfo info) {
            return info.UsagePage == 0xFF01 && info.Usage == 0x10 &&
                info.InputLength == 65 && info.OutputLength == 65;
        }

        static bool ProductIs(HidInfo info, string name) {
            return String.Equals(info.Product,name,StringComparison.OrdinalIgnoreCase);
        }

        static bool OnCollection(HidInfo info, int iface, int collection) {
            string port = "&mi_"+iface.ToString("x2")+"&col"+collection.ToString("x2")+"#";
            return (info.Path ?? "").IndexOf(port,StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static BatteryState ParseS9(byte[] frame) {
            if (frame == null || frame.Length < 16 || frame.Length > 34 || frame[0] != 0 ||
                frame[1] != 0x30 || frame[2] != 0 || frame[4] < 11 || frame[4] > frame.Length-5 ||
                frame[5] != 'M' || frame[6] != '1' || frame[7] != '5' || frame[8] != '6' ||
                frame[14] > 100 || frame[15] != 1) return null;
            return ExpandedUsbBattery.Level(frame[14]);
        }

        internal static BatteryState ParseS9Pro(byte[] frame) {
            if (frame == null || (frame.Length != 16 && frame.Length != 64) ||
                frame[0] != 3 || frame[2] != 0) return null;
            int level = frame[6] & 0x7F;
            bool shortReply = frame[1] == 0x50 && frame[5] == 1 &&
                !frame.Skip(16).Any(value => value != 0);
            bool fullReply = frame.Length == 64 && frame[3] >= 7 && frame[3] <= 58 && frame[4] == 0;
            if ((!shortReply && !fullReply) || level > 100 || (shortReply && level == 0)) return null;
            return ExpandedUsbBattery.Level(level,(frame[6] & 0x80) != 0);
        }

        static BatteryState ReadS9Pro(HidLink link) {
            var request = new byte[64];
            new byte[]{3,0x20,0x50,0,0,0x4F,0x81}.CopyTo(request,0);
            return ParseS9Pro(UsbMouseSupport.RequestFeature(link,request,100));
        }

        internal static BatteryState ParseX23(byte[] frame) {
            if (frame == null || frame.Length != 32 || frame[4] > 100 ||
                !frame.Skip(1).Any(value => value != 0)) return null;
            return ExpandedUsbBattery.Level(frame[4]);
        }

        static BatteryState ReadX23(HidLink link) {
            return ExpandedUsbBattery.Listen(link,
                frame => frame.Length == 32 && HidNative.HasReport(link.Handle,0,frame[0],32) ? ParseX23(frame) : null,5500);
        }

        internal static bool Command65(byte[] frame, byte prefix, byte command) {
            return frame != null && frame.Length == 65 && frame[0] == 0 &&
                frame[1] == prefix && frame[2] == command;
        }

        internal static bool MchoseOnline(byte[] frame) {
            return Command65(frame,0xAA,0xED) && (frame[9] == 0 || frame[9] == 2);
        }

        internal static BatteryState ParseMchoseG3(byte[] frame) {
            if (!Command65(frame,0xAA,0x30) || frame[9] < 1 || frame[9] > 100 || frame[10] > 1) return null;
            int percent = Math.Max(10,((frame[9]+5)/10)*10);
            return ExpandedUsbBattery.Level(percent,frame[10] == 1,true);
        }

        static BatteryState ReadMchoseG3(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0,65) || !HidNative.HasReport(link.Handle,0,0,65)) return null;
            link.Write(new byte[]{0,0x55,0xED});
            if (link.ReadReply(MchoseOnline,550) == null) return null;
            return ExpandedUsbBattery.Ask(link,new byte[]{0,0x55,0x30,0xA5,0x0B,0x2E,1,1,1},ParseMchoseG3,550,0);
        }

        internal static BatteryState ParseDragonfly3Se(byte[] frame) {
            if (frame == null || frame.Length != 65 || frame[0] != 0 || frame[2] != 0x30 ||
                frame[9] > 100 || frame[10] == 1 ||
                (frame[1] == 0x55 && !frame.Skip(3).Any(value => value != 0))) return null;
            return ExpandedUsbBattery.Level(frame[9]);
        }

        static UsbBatteryDevice VgnRyMouse(ushort pid, string name) {
            return UniqueDevice(0x391D,pid,name,
                info => info.UsagePage == 0xFFFF && info.Usage == 2 && info.FeatureLength == 65 &&
                    (pid == 0x1A04 || OnCollection(info,1,1)),ReadVgnRy,readDpi:VgnMouseDpi.ReadRy);
        }

        internal static BatteryState ParseVgnRy(byte[] frame) {
            if (frame == null || frame.Length != 65 || frame[0] != 0 || frame[1] != 0 ||
                frame[3] > 100 || frame[5] != 0 || !frame.Skip(2).Any(value => value != 0)) return null;
            return ExpandedUsbBattery.Level(frame[3]);
        }

        static BatteryState ReadVgnRy(HidLink link) {
            var request = new byte[65];
            request[1] = 0xF7;
            request[8] = 0x08; // Bit7 complement checksum, excluding the report ID.
            return ParseVgnRy(UsbMouseSupport.RequestFeature(link,request,60));
        }

        static UsbBatteryDevice Rapoo(ushort pid, string name, bool wireless = true, ushort usage = 2) {
            var model = UniqueDevice(0x24AE,pid,name,
                info => info.UsagePage == 0xFF00 && info.Usage == usage && info.InputLength >= 9 && info.InputLength <= 256,
                ReadRapooPush,true,wireless);
            model.ReadMouseDpi = MouseDpiQueries.ReadRapoo;
            return model;
        }

        internal static BatteryState ParseRapooPush(byte[] frame) {
            if (frame == null || frame.Length < 9 || frame[0] != 7 || frame[8] > 100 ||
                !frame.Skip(1).Any(value => value != 0)) return null;
            byte status = frame[7];
            bool? charging = status >= 1 && status <= 3 ? (bool?)(status != 1) : null;
            return ExpandedUsbBattery.Level(frame[8],charging);
        }

        static BatteryState ReadRapooPush(HidLink link) {
            if (!HidNative.HasReport(link.Handle,0,7,link.Info.InputLength)) return null;
            return ExpandedUsbBattery.Listen(link,ParseRapooPush,3600);
        }

        static IEnumerable<UsbBatteryDevice> RogPair(ushort cable, ushort receiver, string name,
                bool separateAxes = true, int minimumDpi = 100, int maximumDpi = 36000) {
            Func<HidLink,MouseDpiState> readDpi = link => MouseDpiQueries.ReadRog(link,separateAxes,minimumDpi,maximumDpi);
            yield return Rog(cable,name,false,readDpi);
            yield return Rog(receiver,name,true,readDpi);
        }

        static UsbBatteryDevice Rog(ushort product, string name, bool wireless, Func<HidLink,MouseDpiState> readDpi) {
            return UniqueDevice(0x0B05,product,name,
                info => ExpandedUsbBattery.OnInterface(info,0) && ExpandedUsbBattery.VendorPage(info) &&
                    info.InputLength == 65 && info.OutputLength == 65,
                link => ExpandedUsbBattery.Ask(link,new byte[]{0,0x12,7},ParseRogMouse,1000,0),
                wireless:wireless,readDpi:readDpi);
        }

        internal static BatteryState ParseRogMouse(byte[] frame) {
            int percent;
            bool charging;
            return AsusAimPointMouse.TryPower(frame,out percent,out charging)
                ? ExpandedUsbBattery.Level(percent,charging) : null;
        }
    }
}
