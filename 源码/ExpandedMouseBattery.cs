using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    internal static class ExpandedMouseBattery {
        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return new UsbBatteryDevice(0x04A5, 0x800A, "mouse", "卓威 DW 接收器", true,
                info => info.UsagePage == 0xFF03 && info.OutputLength == 16, null) { ReadGroup=ReadZowie };
            foreach (var model in Aerox(0x1838,0x183A,"Aerox 3 Wireless")) yield return model;
            foreach (var model in Aerox(0x1878,0x187A,"Aerox 3 Wireless")) yield return model;
            foreach (var model in Aerox(0x1852,0x1854,"Aerox 5 Wireless")) yield return model;
            foreach (var model in Aerox(0x1858,0x185A,"Aerox 9 Wireless")) yield return model;
            yield return Corsair(0x1B7F, "Corsair Dark Core RGB Pro SE", "mouse");
            yield return Compx(0x1B1C,0x2B2A,"mouse","Corsair Sabre v2 Pro Ultralight",true);
            yield return new UsbBatteryDevice(0x24AE,0x1231,"mouse","雷柏 VT3 PRO",true,RapooChannel,ReadRapoo);
            yield return new UsbBatteryDevice(0x24AE,0x4431,"mouse","雷柏 VT3 PRO",false,RapooChannel,ReadRapoo);
            yield return Compx(0x3554,0xF517,"mouse","前行者 Z7",true);
            yield return Compx(0x3554,0xF505,"mouse","VGN Dragonfly 4K",true);
            yield return Compx(0x3554,0xF506,"mouse","VGN Dragonfly 4K",false);
            yield return Compx(0x3554,0xF503,"mouse","VGN Dragonfly F1 MOBA",true,CompxMouseDpi.ReadVgn);
            yield return Compx(0x3554,0xFB56,"mouse","VGN Dragonfly F2 Master+",true,CompxMouseDpi.ReadVgn);
            yield return Compx(0x3554,0xF58E,"mouse","VXE R1 SE 1K",true,CompxMouseDpi.ReadR1);
            yield return MouseReceiverBattery.UniqueDevice(0x373B,0x1278,"ATK A9 Air+",
                info => info.UsagePage == 0xFF02 && info.Usage == 2 &&
                    info.InputLength == 17 && info.OutputLength == 17,ReadAtkA9,readDpi:CompxMouseDpi.ReadAtkA9);
            foreach (var model in MouseReceiverBattery.Devices()) yield return model;
            foreach (var model in SupplementalMouseBattery.Devices()) yield return model;
            yield return Compx(0x373B,0x11D9,"mouse","ATK A9 Ultimate",true);
            yield return Compx(0x373B,0x11B6,"mouse","ATK A9 Ultimate",false);
            yield return Compx(0x373B,0x10C9,"mouse","ATK A9 SE",true);
            yield return Compx(0x373B,0x101A,"mouse","ATK X1 Pro",true);
            yield return Compx(0x373B,0x1085,"mouse","ATK VXE R1 SE+",true);
            yield return Bloody(0x3069,true);
            yield return Bloody(0x3144,false);
            yield return new UsbBatteryDevice(0x1D57,0xFA65,"mouse",null,true,
                info => info.UsagePage == 0x000A && info.Usage == 0 && info.InputLength >= 5,
                link => ExpandedUsbBattery.Listen(link, ParseInphic, 2300), true);
        }

        static IEnumerable<UsbBatteryDevice> Aerox(ushort receiver, ushort cable, string name) {
            foreach (ushort product in new[]{receiver,cable}) {
                byte command = product == receiver ? (byte)0xD2 : (byte)0x92;
                yield return new UsbBatteryDevice(0x1038,product,"mouse","SteelSeries " + name,product == receiver,
                    info => ExpandedUsbBattery.OnInterface(info, 3) && ExpandedUsbBattery.VendorPage(info) &&
                        info.OutputLength >= 64 && info.InputLength >= 3,
                    link => ExpandedUsbBattery.Ask(link,new byte[]{0,command},frame => ParseAerox(frame,command),750,0));
            }
        }

        internal static BatteryState ParseAerox(byte[] frame, byte command) {
            if (frame == null || frame.Length < 3 || frame[0] != 0 || frame[1] != command) return null;
            int value = frame[2] & 127;
            if (value < 1 || value > 100) return null;
            return ExpandedUsbBattery.Level(value <= 21 ? (value - 1) * 5 : value, (frame[2] & 128) != 0, value <= 21);
        }

        internal static UsbBatteryDevice Compx(ushort vendor, ushort product, string kind, string name, bool wireless,
                Func<HidLink,MouseDpiState> readDpi = null) {
            return new UsbBatteryDevice(vendor,product,kind,name,wireless,
                info => info.UsagePage == 0xFF02 && info.Usage == 2 && info.InputLength == 17 && info.OutputLength == 17,
                link => ReadCompx(link, kind == "keyboard")) { ReadMouseDpi=readDpi };
        }

        internal static byte[] CompxRequest(byte command, bool keyboard) {
            var frame = new byte[17];
            frame[0]=8;
            frame[1]=command;
            frame[5]=keyboard ? (byte)0x80 : (byte)0;
            frame[16]=unchecked((byte)(0x55 - frame[0] - frame[1] - frame[5]));
            return frame;
        }

        static bool ValidCompx(byte[] frame, byte command) {
            return frame != null && frame.Length == 17 && frame[0] == 8 && frame[1] == command && frame[2] == 0 &&
                (frame.Sum(value => (int)value) & 255) == 0x55;
        }

        internal static BatteryState ParseCompx(byte[] frame) {
            if (!ValidCompx(frame,4) || frame[6] > 100 || !frame.Skip(6).Take(10).Any(value => value != 0)) return null;
            return ExpandedUsbBattery.Level(frame[6], frame[7] <= 2 ? (bool?)(frame[7] == 1) : null);
        }

        static BatteryState ReadCompx(HidLink link, bool keyboard) {
            if (keyboard) {
                if (!HidNative.HasReport(link.Handle,1,8,17) || !HidNative.HasReport(link.Handle,0,8,17)) return null;
                link.Write(CompxRequest(3,true));
                var connected = link.ReadReply(frame => ValidCompx(frame,3) && frame[6] != 0,550);
                if (connected == null || connected[6] != 1) return null;
            }
            return ExpandedUsbBattery.Ask(link,CompxRequest(4,keyboard),ParseCompx,750,8);
        }

        static BatteryState ReadAtkA9(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,8,17) || !HidNative.HasReport(link.Handle,0,8,17)) return null;
            link.Write(CompxRequest(1,false));
            if (link.ReadReply(frame => ValidCompx(frame,1),500) == null) return null;
            return ExpandedUsbBattery.Ask(link,CompxRequest(4,false),
                frame => frame.Length == 17 && frame[7] <= 1 ? ParseCompx(frame) : null,750,8);
        }

        internal static UsbBatteryDevice Corsair(ushort product, string name, string kind) {
            return new UsbBatteryDevice(0x1B1C,product,kind,name,true,
                info => info.UsagePage == 0xFF42 && info.Usage == 1 && info.InputLength >= 64 && info.OutputLength == 65,
                ReadCorsair);
        }

        internal static int? ParseCorsair(byte[] frame) {
            if (frame == null || frame.Length < 5) return null;
            int start = frame[0] == 0 ? 1 : 0;
            if (frame.Length < start + 5 || frame[start] != 1 || frame[start+1] != 2 || frame[start+2] != 0) return null;
            int value = frame[start+3] | (frame[start+4] << 8);
            return value > 0 && value <= 1000 ? (int?)value : null;
        }

        static int? CorsairLevel(HidLink link, int timeout) {
            link.Write(new byte[]{0,9,2,0x0F});
            return ParseCorsair(link.ReadReply(frame => ParseCorsair(frame).HasValue, timeout));
        }

        static BatteryState ReadCorsair(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0,link.Info.OutputLength)) return null;
            int? first = CorsairLevel(link,5400);
            return first.HasValue && CorsairLevel(link,1500) == first ? ExpandedUsbBattery.Level(first.Value / 10) : null;
        }

        static bool RapooChannel(HidInfo info) {
            return ExpandedUsbBattery.VendorPage(info) && info.OutputLength == 33 && info.InputLength >= 4;
        }

        internal static BatteryState ParseRapoo(byte[] frame) {
            if (frame == null || frame.Length < 4 || frame[0] != 0xBA || frame[1] != 1 ||
                (frame[2] != 1 && frame[2] != 2) || frame[3] > 100) return null;
            return ExpandedUsbBattery.Level(frame[3],frame[2] == 2);
        }

        static bool RapooReports(HidLink link) {
            return HidNative.HasReport(link.Handle,1,0xBA,link.Info.OutputLength) &&
                HidNative.HasReport(link.Handle,0,0xBA,link.Info.InputLength);
        }

        internal static byte[] RapooRequest(bool cable, byte command, int address = 0, byte count = 0) {
            byte[] frame = new byte[33];
            frame[0] = 0xBA;
            frame[1] = cable ? (byte)0xFF : (byte)0xA5;
            frame[2] = command;
            frame[3] = count;
            frame[4] = (byte)address;
            frame[5] = (byte)(address >> 8);
            return frame;
        }

        static byte[] AskRapoo(HidLink link, byte command, int address = 0, byte count = 0) {
            var request = RapooRequest(link.Info.Pid == 0x4431,command,address,count);
            var timer = Stopwatch.StartNew();
            for (int attempt = 0; attempt < 3 && timer.ElapsedMilliseconds < 850; attempt++) {
                link.Write(request);
                var sent = timer.ElapsedMilliseconds;
                bool busy = false;
                while (timer.ElapsedMilliseconds < 850) {
                    var reply = link.ReadInputReport(0xBA);
                    if (reply == null) return null;
                    if (reply[1] == 2) busy = true;
                    if (reply[1] == 1 && busy) {
                        Thread.Sleep(50);
                        return reply;
                    }
                    if (!busy && timer.ElapsedMilliseconds - sent >= 110) break;
                    Thread.Sleep(2);
                }
            }
            return null;
        }

        static BatteryState ReadRapoo(HidLink link) {
            return RapooReports(link) ? ParseRapoo(AskRapoo(link,0xAA)) : null;
        }

        static byte[] ReadRapooBlock(HidLink link, int address, byte count) {
            // A marked battery reply prevents a cached reply from being mistaken for a block.
            if (ParseRapoo(AskRapoo(link,0xAA)) == null) return null;
            var block = AskRapoo(link,0xA4,address,count);
            return block != null && block.Length >= 5+count && block[2] == 0
                ? block.Skip(5).Take(count).ToArray() : null;
        }

        internal static MouseDpiState ParseRapooDpi(byte[] x, byte[] y, byte[] selected) {
            if (x == null || y == null || selected == null || x.Length != 16 || y.Length != 16 || selected.Length != 4 ||
                selected[0] >= 7) return null;
            int active = selected[0];
            int xDpi = x[active*2] | (x[active*2+1] << 8), yDpi = y[active*2] | (y[active*2+1] << 8);
            if (!ValidDpi(xDpi) || !ValidDpi(yDpi)) return null;
            int count = x[14];
            var current = new MouseDpiState { Mode=1, CanSwitch=false, Current=xDpi, CurrentVertical=yDpi };
            if (count < 1 || count > 7 || active >= count || (y[14] != 0 && y[14] != count)) return current;
            int[] horizontal = new int[count], vertical = new int[count];
            for (int stage = 0; stage < count; stage++) {
                horizontal[stage] = x[stage*2] | (x[stage*2+1] << 8);
                vertical[stage] = y[stage*2] | (y[stage*2+1] << 8);
                if (!ValidDpi(horizontal[stage]) || !ValidDpi(vertical[stage])) return current;
            }
            current.Index = active;
            current.Presets = horizontal;
            current.VerticalPresets = vertical;
            return current;
        }

        static bool ValidDpi(int dpi) { return dpi >= 50 && dpi <= 26000 && dpi % 50 == 0; }

        internal static bool SupportsDpi(string name) { return name == "雷柏 VT3 PRO"; }

        internal static HidInfo FindDpiChannel(IEnumerable<HidInfo> devices, string key) {
            return devices.FirstOrDefault(info => info.Transport == "usb" && info.Vid == 0x24AE &&
                (info.Pid == 0x1231 || info.Pid == 0x4431) && RapooChannel(info) &&
                BatteryReader.UsbDeviceKey(info,"mouse") == key);
        }

        internal static MouseDpiState ReadDpi(HidLink link) {
            if (!RapooReports(link)) return null;
            var selected = ReadRapooBlock(link,0x898,4);
            if (selected == null) return null;
            var x = ReadRapooBlock(link,0x888,16);
            if (x == null) return null;
            var y = ReadRapooBlock(link,0x8C8,16);
            if (y == null) return null;
            var after = ReadRapooBlock(link,0x898,4);
            return after != null && after[0] == selected[0] ? ParseRapooDpi(x,y,selected) : null;
        }

        internal static BatteryState ParseInphic(byte[] frame) {
            if (frame == null || frame.Length < 5 || frame[0] != 3 || frame[2] != 0x40) return null;
            if (frame[1] != 0x95 && frame[1] != 0x90 && frame[1] != 0x93 && frame[1] != 0x99) return null;
            BatteryState reading;
            if (frame[3] == 2) reading = ExpandedUsbBattery.Level(100,false);
            else if (frame[3] == 3) reading = new BatteryState { Charging=true, Detail="设备报告正在充电" };
            else if (frame[3] == 1 && frame[4] > 0 && frame[4] <= 100) reading = ExpandedUsbBattery.Level(frame[4],false);
            else return null;
            reading.Name = "英菲克 IN9 Pro";
            return reading;
        }

        static UsbBatteryDevice Bloody(ushort product, bool wireless) {
            return new UsbBatteryDevice(0x1956,product,"mouse","双飞燕 Bloody SG5 Pro",wireless,
                info => info.UsagePage == 0xFF02 && info.Usage == 1 &&
                    ExpandedUsbBattery.OnInterface(info,2) && info.FeatureLength == 9,null) { ReadGroup=ReadBloody };
        }

        internal static BatteryState ParseBloody(byte[] frame) {
            if (frame == null || frame.Length != 8 || frame[0] != 6 || frame[1] != 0x56 ||
                frame[2] > 100 || frame[3] > 1) return null;
            return ExpandedUsbBattery.Level(frame[2],frame[3] == 1 && frame[2] < 100);
        }

        static BatteryState ReadBloody(HidLink command, IEnumerable<HidInfo> group) {
            var status = group.FirstOrDefault(info => info.UsagePage == 0xFF01 && info.Usage == 1 &&
                info.InputLength == 8 && info.Vid == command.Info.Vid && info.Pid == command.Info.Pid);
            if (status == null) return null;
            using (var reader = new HidLink(status,true)) {
                if (!HidNative.HasReport(reader.Handle,0,6,8) ||
                    !command.WriteFeature(new byte[]{0,0x80,0,0,0,0,0,0,0x7F})) return null;
                Thread.Sleep(20);
                return command.WriteFeature(new byte[]{0,0x80,0xFF,0,0,0,0,0,0x80})
                    ? ExpandedUsbBattery.Listen(reader,ParseBloody,1000) : null;
            }
        }

        internal static int? ParseZowie(byte[] frame, byte command) {
            int marker = command == 0x25 ? 3 : 4;
            if (frame == null || frame.Length != 16 || frame[0] != 9 || frame[marker] != 0xC8 ||
                (marker == 4 && frame[3] != 0) || frame.Skip(marker+1).Any(padding => padding != 0)) return null;
            int value = (frame[1] << 8) | frame[2];
            if (command == 0x15) return value > 300 && value < 500 ? (int?)value : null;
            return (frame[1] & 15) <= 9 && (frame[1] >> 4) <= 9 &&
                (frame[2] & 15) <= 9 && (frame[2] >> 4) <= 9 ? (int?)value : null;
        }

        static int? ZowieQuery(HidLink output, HidLink input, byte command) {
            output.Write(new byte[]{8,0xB4,command});
            return ParseZowie(input.ReadReply(frame => ParseZowie(frame,command).HasValue,700),command);
        }

        static BatteryState ReadZowie(HidLink output, IEnumerable<HidInfo> group) {
            var input = group.Where(info => info.Vid == 0x04A5 && info.Pid == 0x800A &&
                info.UsagePage == 0xFF04 && info.InputLength == 16).ToArray();
            if (input.Length != 1 || group.Count(info => info.UsagePage == 0xFF03 && info.OutputLength == 16) != 1 ||
                !HidNative.HasReport(output.Handle,1,8,16)) return null;
            using (var reader = new HidLink(input[0],true)) {
                if (!HidNative.HasReport(reader.Handle,0,9,16)) return null;
                var receiver = ZowieQuery(output,reader,0x40);
                if (receiver != 0x8511 && receiver != 0x8507) return null;
                int? before = ZowieQuery(output,reader,0x25);
                if (!before.HasValue || before.Value == 0) return null;
                int? voltage = ZowieQuery(output,reader,0x15);
                if (!voltage.HasValue || ZowieQuery(output,reader,0x25) != before) return null;
                return new BatteryState { Detail="接收器缓存电压 " + (voltage.Value / 100.0).ToString("0.00",CultureInfo.InvariantCulture) + " V" };
            }
        }
    }
}
