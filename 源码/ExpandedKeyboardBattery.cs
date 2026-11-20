using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery {
    internal static class ExpandedKeyboardBattery {
        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return new UsbBatteryDevice(0x046A,0x01E4,"keyboard","CHERRY PIXIU75",true,CherryChannel,ReadCherry);
            yield return new UsbBatteryDevice(0x046A,0x01E2,"keyboard","CHERRY PIXIU75",false,CherryChannel,ReadCherry);
            yield return Report4Keyboard(0x046A,0x01E0,"CHERRY MX 2.0S WL Pro","MX 2.0S Pro Dongle",true,true);
            yield return Report4Keyboard(0x046A,0x01DE,"CHERRY MX 2.0S WL Pro","MX 2.0S WL Pro",false,true);
            yield return ExpandedMouseBattery.Corsair(0x2B07,"Corsair K65 PLUS Wireless","keyboard");
            yield return Rog(0x1A83,"ROG Azoth",6,false);
            yield return Rog(0x1A85,"ROG Azoth",6,true);
            yield return Rog(0x1AAE,"ROG Strix Scope II 96 Wireless",6,false);
            yield return Rog(0x1AAF,"ROG Strix Scope II 96 Wireless",6,true);
            yield return Rog(0x193C,"ROG Falchion",11,false);
            yield return Rog(0x193E,"ROG Falchion",11,true);
            yield return new UsbBatteryDevice(0x3554,0xFA09,"keyboard",null,true,
                info => RadioFrames(info) && info.UsagePage == 0xFF02 && info.Usage == 2,ReadAula);
            yield return UniqueKeyboard(0x0C45,0xFEFE,"AULA F87 Pro V2",true,
                info => AulaHfdChannel(info) && ProductIs(info,"AULA F87ProV2D Dongle") &&
                    ExpandedUsbBattery.OnInterface(info,3),ReadAulaF87V2);
            yield return Vgn(0x391D,0x2A02,"VGN V108",true);
            yield return Vgn(0x391D,0x2A01,"VGN N75 V2");
            yield return Vgn(0x258A,0x027F,"VGN V98 Pro V3");
            yield return Vgn(0x258A,0x027B,"VGN V87 V2");
            yield return UniqueKeyboard(0x258A,0x024F,"VGN V98 Pro V3",true,
                info => RadioFrames(info) && info.UsagePage == 0xFF02 && info.Usage == 2,
                link => ExpandedUsbBattery.Ask(link,MchoseKeyboard.RadioRequest(0x4A),ParseVgnRadioLevel,1000,0x13));
            yield return Report4Keyboard(0x320F,0x5088,null,"Wireless Gaming Keyboard",true,false);
            yield return Report4Keyboard(0x373B,0x1012,"ATK Z87 Pro","ATK Z87 Dongle",true,false);
            yield return ExpandedMouseBattery.Compx(0x05AC,0x024F,"keyboard","Lofree Flow Lite84",true);
            yield return UniqueKeyboard(0x05AC,0x024F,"AULA F99 MAX",true,
                info => AulaHfdChannel(info) && ProductIs(info,"F99 MAX Dongle"),ReadAulaF99Max);
            yield return HyzenKeyboardBattery.Receiver();
            foreach (var model in MchoseV3KeyboardBattery.Devices()) yield return model;
            foreach (var model in EweadnKeyboardBattery.Devices()) yield return model;
        }

        internal static UsbBatteryDevice UniqueKeyboard(ushort vendor, ushort product, string name, bool wireless,
                Func<HidInfo,bool> channel, Func<HidLink,BatteryState> read) {
            var model = new UsbBatteryDevice(vendor,product,"keyboard",name,wireless,channel,read);
            model.ReadGroup = (link,interfaces) => interfaces.Count(model.Matches) == 1 ? read(link) : null;
            return model;
        }

        internal static bool ProductIs(HidInfo info, string product) {
            return String.Equals(info.Product,product,StringComparison.OrdinalIgnoreCase);
        }

        static bool CherryChannel(HidInfo info) {
            return ExpandedUsbBattery.Page(info,0xFF1C) && info.Usage == 0x0092 &&
                info.OutputLength == 64 && info.InputLength >= 10;
        }

        internal static BatteryState ParseCherry(byte[] frame) {
            if (frame == null || frame.Length < 12 || frame[0] != 4 || frame[3] != 0x1A ||
                frame[8] > 100 || !frame.Skip(8).Take(4).Any(value => value != 0)) return null;
            return ExpandedUsbBattery.Level(frame[8],(frame[9] & 1) != 0);
        }

        static BatteryState ReadCherry(HidLink link) {
            return ExpandedUsbBattery.Ask(link,new byte[]{4,0,0,0x1A,4},ParseCherry,1000,4);
        }

        static UsbBatteryDevice Report4Keyboard(ushort vendor, ushort product, string name, string label,
                bool wireless, bool reportsCharging) {
            var request = new byte[]{4,reportsCharging ? (byte)0x20 : (byte)0,0,0x1A,6};
            return UniqueKeyboard(vendor,product,name,wireless,
                info => ProductIs(info,label) && info.UsagePage == 0xFF1C && info.Usage == 0x92 &&
                    info.InputLength == 64 && info.OutputLength == 64 &&
                    (!reportsCharging || ExpandedUsbBattery.OnInterface(info,1)),
                link => ExpandedUsbBattery.Ask(link,request,frame => ParseReport4Power(frame,reportsCharging),1000,4));
        }

        internal static BatteryState ParseReport4Power(byte[] frame, bool reportsCharging) {
            if (frame == null || frame.Length != 64 || frame[0] != 4 ||
                frame[1] != (reportsCharging ? 0x20 : 0) || frame[2] != 0 || frame[3] != 0x1A ||
                frame[4] != 6 || frame[8] == 0 || frame[8] > 100) return null;
            if (reportsCharging && (frame[7] != 0 || frame[9] > 1)) return null;
            return ExpandedUsbBattery.Level(frame[8],reportsCharging ? (bool?)(frame[9] == 1) : null);
        }

        static UsbBatteryDevice Rog(ushort product, string name, int levelIndex, bool wireless) {
            return new UsbBatteryDevice(0x0B05,product,"keyboard",name,wireless,
                info => ExpandedUsbBattery.OnInterface(info,1) && ExpandedUsbBattery.VendorPage(info) &&
                    info.InputLength >= 12 && info.OutputLength >= 64,
                link => ExpandedUsbBattery.Ask(link,new byte[]{0,0x12,1},frame => ParseRog(frame,levelIndex),1000,0));
        }

        internal static BatteryState ParseRog(byte[] frame, int levelIndex) {
            if (frame == null || frame.Length < 12 || frame[0] != 0 || frame[1] != 0x12 || frame[2] != 1 ||
                frame[levelIndex] > 100 || !frame.Skip(3).Any(value => value != 0)) return null;
            return ExpandedUsbBattery.Level(frame[levelIndex],frame[9] == 1);
        }

        static bool RadioFrames(HidInfo info) { return info.InputLength == 20 && info.OutputLength == 20; }

        static UsbBatteryDevice Vgn(ushort vendor, ushort product, string name, bool interfaceOne = false) {
            return new UsbBatteryDevice(vendor,product,"keyboard",name,true,
                info => info.InputLength >= 20 && info.OutputLength >= 20 &&
                    ((ExpandedUsbBattery.Page(info,0xFF02) && info.Usage == 2) ||
                    (interfaceOne && ExpandedUsbBattery.OnInterface(info,1) &&
                    (info.UsagePage != 0x0001 || info.Usage == 0) && info.UsagePage != 0x000C)),
                link => ExpandedUsbBattery.Ask(link,MchoseKeyboard.RadioRequest(0x4A),frame => ParseRadioLevel(frame,false),1000,0x13));
        }

        static bool ValidRadio(byte[] frame) {
            return frame != null && frame.Length >= 20 && frame[0] == 0x13 && frame[2] == 1 &&
                frame[3] == 0 && (frame.Take(19).Sum(value => (int)value) & 255) == frame[19];
        }

        internal static BatteryState ParseRadioLevel(byte[] frame, bool allowPush) {
            if (!ValidRadio(frame)) return null;
            int offset = frame[1] == 0x4A && frame[4] == 2 ? 5 : -1;
            if (allowPush && frame[1] == 0x0A && frame[4] == 4 && frame[5] == 5) offset = 6;
            if (offset < 0 || frame[offset] > 100 || frame[offset] == 0) return null;
            bool charging = (frame[offset+1] >> 4) == 1 && (frame[offset+1] & 15) != 1;
            return ExpandedUsbBattery.Level(frame[offset],charging);
        }

        internal static BatteryState ParseVgnRadioLevel(byte[] frame) {
            if (!ValidRadio(frame) || frame.Length != 20 || frame[1] != 0x4A || frame[4] != 2 ||
                frame[5] > 100) return null;
            return ExpandedUsbBattery.Level(frame[5],(frame[6] & 0x10) != 0);
        }

        internal static string ParseAulaModel(byte[] frame) {
            if (!ValidRadio(frame) || frame[1] != 5 || !frame.Skip(5).Take(4).SequenceEqual(new byte[]{3,0,0,0}))
                return null;
            int code = (frame[9] << 8) | frame[10];
            if (code == 0x0158) return "AULA F99 Pro";
            if (code == 0x0115) return "MCHOSE G75 Pro";
            if (code == 0x01C7) return "MCHOSE KX75";
            return null;
        }

        internal static bool IsF87ProLabel(string label) {
            string name = new string((label ?? "").Where(Char.IsLetterOrDigit).Select(Char.ToUpperInvariant).ToArray());
            return name == "F87PRO" || name == "AULAF87PRO" || name == "F87PRODONGLE" || name == "AULAF87PRODONGLE";
        }

        static BatteryState ReadAula(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0x13,20) || !HidNative.HasReport(link.Handle,0,0x13,20)) return null;
            if (IsF87ProLabel(link.Info.Product)) {
                var f87 = ExpandedUsbBattery.Listen(link,frame => ParseRadioLevel(frame,true),550) ??
                    ExpandedUsbBattery.Ask(link,MchoseKeyboard.RadioRequest(0x4A),frame => ParseRadioLevel(frame,true),800,0x13);
                if (f87 != null) f87.Name = "AULA F87 Pro";
                return f87;
            }
            link.Write(MchoseKeyboard.RadioRequest(5));
            string name = null;
            BatteryState reading = null;
            link.ReadReply(frame => {
                name = ParseAulaModel(frame) ?? name;
                reading = ParseRadioLevel(frame,true) ?? reading;
                return name != null && reading != null;
            },600);
            if (reading == null)
                reading = ExpandedUsbBattery.Ask(link,MchoseKeyboard.RadioRequest(0x4A),frame => ParseRadioLevel(frame,true),800,0x13);
            if (name == null) return reading;
            reading = reading ?? new BatteryState { Detail="键盘暂未返回电量" };
            reading.Name = name;
            reading.DeviceKey = WindowsDevices.PrivateKey(BatteryReader.UsbDeviceKey(link.Info,"keyboard") + "|" + name);
            return reading;
        }

        static bool AulaHfdChannel(HidInfo info) {
            return info.UsagePage == 0xFF60 && info.Usage == 0x61 &&
                info.InputLength == 33 && info.OutputLength == 33;
        }

        static bool HfdPage(byte[] frame, byte address) {
            return frame != null && frame.Length == 33 && frame[0] == 0 &&
                frame[1] == 0x55 && frame[2] == 0x10 && frame[3] == 0x18 && frame[4] == address;
        }

        internal static BatteryState ParseAulaF87V2(byte[] frame) {
            return HfdPage(frame,0) && frame[26] <= 100 && frame.Skip(5).Any(value => value != 0)
                ? ExpandedUsbBattery.Level(frame[26]) : null;
        }

        static BatteryState ReadAulaF87V2(HidLink link) {
            var request = new byte[]{0,0xAA,0x10,0x18,0};
            return ExpandedUsbBattery.Ask(link,request,ParseAulaF87V2,900,0);
        }

        internal static bool IsF99FirstPage(byte[] frame) {
            return HfdPage(frame,0) && frame[13] == 0x45 && frame[14] == 0x0C &&
                frame[15] == 0x0A && frame[16] == 0x80 && frame[26] > 0 && frame[26] <= 100 && frame[27] <= 1;
        }

        internal static BatteryState ParseAulaF99Max(byte[] first, byte[] second) {
            return IsF99FirstPage(first) && HfdPage(second,0x18) &&
                second[5] == 0 && second[6] == 0 && second[7] == 1
                ? ExpandedUsbBattery.Level(first[26]) : null;
        }

        static BatteryState ReadAulaF99Max(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0,33) || !HidNative.HasReport(link.Handle,0,0,33)) return null;
            link.Write(new byte[]{0,0xAA,0x10,0x18,0});
            var first = link.ReadReply(IsF99FirstPage,850);
            return first == null ? null : ExpandedUsbBattery.Ask(link,new byte[]{0,0xAA,0x10,0x18,0x18,0,0,1},
                second => ParseAulaF99Max(first,second),850,0);
        }
    }
}
