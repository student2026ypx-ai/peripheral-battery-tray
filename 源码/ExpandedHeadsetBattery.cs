using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PeripheralBattery {
    internal static class ExpandedHeadsetBattery {
        internal enum SteelSeriesReply { NovaPercent, NovaSteps, Nova5, GameBuds, Arctis7Plus }

        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return Steel(0x2202,"Arctis Nova 7",SteelSeriesReply.NovaSteps);
            yield return Steel(0x22A1,"Arctis Nova 7",SteelSeriesReply.NovaPercent);
            yield return Steel(0x227E,"Arctis Nova 7 Gen 2",SteelSeriesReply.NovaPercent);
            yield return Steel(0x2206,"Arctis Nova 7X",SteelSeriesReply.NovaSteps);
            yield return Steel(0x22A5,"Arctis Nova 7X",SteelSeriesReply.NovaPercent);
            yield return Steel(0x220A,"Arctis Nova 7P",SteelSeriesReply.NovaSteps);
            yield return Steel(0x22A7,"Arctis Nova 7P",SteelSeriesReply.NovaPercent);
            yield return Steel(0x2232,"Arctis Nova 5",SteelSeriesReply.Nova5);
            yield return Steel(0x2253,"Arctis Nova 5X",SteelSeriesReply.Nova5);
            yield return Steel(0x2269,"Arctis Nova 3P Wireless",SteelSeriesReply.Nova5);
            yield return Steel(0x230A,"Arctis GameBuds",SteelSeriesReply.GameBuds);
            yield return Steel(0x2317,"Arctis GameBuds X",SteelSeriesReply.GameBuds);
            yield return Rog(0x1AFA,"ROG Delta II");
            yield return Rog(0x1D41,"ROG Delta II");
            yield return Rog(0x1B84,"ROG Pelta");
            yield return new UsbBatteryDevice(0x0B05,0x1AD3,"headset","ROG Cetra True Wireless SpeedNova",true,
                info => ExpandedUsbBattery.Page(info,0xFF00) && info.InputLength >= 9 && info.OutputLength == 64,
                ReadCetra);
            yield return new UsbBatteryDevice(0x0B05,0x18D6,"headset","ROG Strix Go 2.4",true,
                info => ExpandedUsbBattery.VendorPage(info) && info.FeatureLength == 64,ReadStrixGo);
            yield return new UsbBatteryDevice(0x1B1C,0x0A55,"headset","Corsair Void Elite Wireless",true,
                info => ExpandedUsbBattery.Page(info,0xFFC5) && info.Usage == 1 && ExpandedUsbBattery.OnInterface(info,3) &&
                    info.InputLength >= 5 && info.OutputLength >= 2,
                link => ExpandedUsbBattery.Ask(link,new byte[]{0xC9,0x64},ParseVoid,1000,0x64));
            yield return new UsbBatteryDevice(0x03F0,0x098D,"headset","HyperX Cloud Alpha Wireless",true,
                info => ExpandedUsbBattery.VendorPage(info) && info.InputLength >= 4 && info.OutputLength >= 31,ReadCloudAlpha);
            yield return Cloud3(0x05B7,"HyperX Cloud III Wireless");
            yield return Cloud3(0x0C9D,"HyperX Cloud III Wireless");
            yield return Cloud3(0x0995,"HyperX Cloud II Core Wireless");
            yield return Cloud3(0x0795,"HyperX Cloud II Core Wireless",false);
            yield return new UsbBatteryDevice(0x38A6,0x0030,"headset","AULA G9",true,
                info => ExpandedUsbBattery.Page(info,0xFFAB) && info.InputLength >= 8,
                link => ExpandedUsbBattery.Listen(link,ParseAula,1750),true);
            foreach (var model in SteelSeriesHeadsetBattery.Devices()) yield return model;
            foreach (var model in HeadsetReceiverBattery.Devices()) yield return model;
        }

        internal static UsbBatteryDevice Steel(ushort product, string name, SteelSeriesReply format) {
            return new UsbBatteryDevice(0x1038,product,"headset","SteelSeries " + name,true,
                info => ExpandedUsbBattery.Page(info,0xFFC0) && ExpandedUsbBattery.OnInterface(info,3) &&
                    info.OutputLength >= 2 && info.InputLength >= 4,
                link => ExpandedUsbBattery.Ask(link,new byte[]{0,0xB0},frame => ParseSteel(frame,format),1000));
        }

        internal static BatteryState ParseSteel(byte[] frame, SteelSeriesReply format) {
            if (frame == null || frame.Length < 4) return null;
            int start = frame[0] == 0 ? 1 : 0;
            if (frame.Length < start+4 || frame[start] != 0xB0) return null;
            if (format == SteelSeriesReply.GameBuds) {
                if (frame.Length < start+7) return null;
                var levels = Enumerable.Range(0,2).Where(i => frame[start+3+i] == 3 && frame[start+5+i] <= 100)
                    .Select(i => frame[start+5+i]).ToArray();
                return levels.Length == 0 ? null : ExpandedUsbBattery.Level(levels.Min());
            }
            if (format == SteelSeriesReply.Nova5) {
                if (frame.Length < start+5 || frame[start+1] == 2 ||
                    frame[start+3] > 100 || !frame.Skip(start+1).Any(value => value != 0)) return null;
                return ExpandedUsbBattery.Level(frame[start+3],frame[start+4] == 1);
            }
            if (format == SteelSeriesReply.Arctis7Plus) {
                if (frame[start+1] == 1 || frame[start+2] > 4 ||
                        (frame[start+1] == 0 && frame[start+2] == 0 && frame[start+3] == 0)) return null;
                bool? charging = frame[start+3] <= 1 ? (bool?)(frame[start+3] == 1) : null;
                return ExpandedUsbBattery.Level(25*frame[start+2],charging,true);
            }
            bool steps = format == SteelSeriesReply.NovaSteps;
            int level = frame[start+2], state = frame[start+3];
            if (frame[start+1] != 3 || state < 1 || state > 3 || level > (steps ? 4 : 100)) return null;
            return ExpandedUsbBattery.Level(steps ? level * 25 : level,state != 3,steps);
        }

        static UsbBatteryDevice Rog(ushort product, string name) {
            return new UsbBatteryDevice(0x0B05,product,"headset",name,true,
                info => ExpandedUsbBattery.Page(info,0xFF00) && info.InputLength >= 7 && info.OutputLength >= 64,ReadRog);
        }

        internal static BatteryState ParseRog(byte[] frame) {
            if (frame == null || frame.Length < 7 || frame[0] != 0xCC || frame[1] != 0x12 || frame[2] != 7 ||
                frame[6] > 100 || !frame.Skip(3).Any(value => value != 0)) return null;
            return ExpandedUsbBattery.Level(frame[6]);
        }

        static BatteryState ReadRog(HidLink link) {
            var reading = ExpandedUsbBattery.Ask(link,new byte[]{0xCC,0x12,7},ParseRog,850,0xCC);
            if (reading == null) return null;
            link.Write(new byte[]{0xCC,0x12,8});
            var charge = link.ReadReply(frame => frame.Length >= 6 && frame[0] == 0xCC && frame[1] == 0x12 &&
                frame[2] == 8 && frame[5] <= 1,550);
            if (charge != null) reading.Charging = charge[5] == 1;
            return reading;
        }

        internal static BatteryState ParseCetra(byte[] frame) {
            if (frame == null || frame.Length < 9 || frame[0] != 0xCC || frame[1] != 0x12 ||
                frame[2] != 7 || !frame.Skip(3).Any(value => value != 0)) return null;
            var levels = new[]{frame[6],frame[7]}.Where(value => value <= 100).ToArray();
            return levels.Length == 0 ? null : ExpandedUsbBattery.Level(levels.Min());
        }

        static BatteryState ReadCetra(HidLink link) {
            var reading = ExpandedUsbBattery.Ask(link,new byte[]{0xCC,0x12,7},ParseCetra,850,0xCC);
            if (reading == null) return null;
            link.Write(new byte[]{0xCC,0x12,8});
            var charge = link.ReadReply(frame => frame.Length >= 6 && frame[0] == 0xCC &&
                frame[1] == 0x12 && frame[2] == 8 && frame[5] != 0xFF,550);
            if (charge != null) reading.Charging = (charge[5]&3) != 0;
            return reading;
        }

        internal static BatteryState ParseStrixGo(byte[] frame) {
            if (frame == null || frame.Length != 64 || frame[0] != 0xFF || frame[1] != 0x1B) return null;
            int millivolts = (frame[12]<<8)|frame[11];
            if (millivolts < 2800 || millivolts > 4600) return null;
            int percent = millivolts >= 4100 ? 100 : millivolts >= 3800 ? 75 : millivolts >= 3700 ? 50 :
                millivolts >= 3500 ? 25 : 10;
            return ExpandedUsbBattery.Level(percent,frame[9] == 0x0A,true);
        }

        static BatteryState ReadStrixGo(HidLink link) {
            var request = new byte[64];
            new byte[]{0xFF,8,0,0xFD,4,0x12,0xF1,3,0x52,1}.CopyTo(request,0);
            return ParseStrixGo(UsbMouseSupport.RequestFeature(link,request,55));
        }

        internal static BatteryState ParseVoid(byte[] frame) {
            if (frame == null || frame.Length < 5 || frame[0] != 0x64 || frame[3] != 0xB1 ||
                (frame[4] != 1 && frame[4] != 2 && frame[4] != 4 && frame[4] != 5)) return null;
            int percent = frame[2] & 127;
            return percent <= 100 ? ExpandedUsbBattery.Level(percent,frame[4] >= 4) : null;
        }

        static bool CloudAlphaFrame(byte[] frame, byte command) {
            return frame != null && frame.Length >= 4 && frame[0] == 0x21 && frame[1] == 0xBB && frame[2] == command;
        }

        internal static BatteryState ParseCloudAlpha(byte[] frame) {
            return CloudAlphaFrame(frame,0x0B) && frame[3] <= 100 ? ExpandedUsbBattery.Level(frame[3]) : null;
        }

        static BatteryState ReadCloudAlpha(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0x21,link.Info.OutputLength) ||
                !HidNative.HasReport(link.Handle,0,0x21,link.Info.InputLength)) return null;
            link.Write(new byte[]{0x21,0xBB,3});
            var online = link.ReadReply(frame => CloudAlphaFrame(frame,3),550);
            if (online == null || online[3] == 1) return null;
            var battery = ExpandedUsbBattery.Ask(link,new byte[]{0x21,0xBB,0x0B},ParseCloudAlpha,750,0x21);
            if (battery == null) return null;
            link.Write(new byte[]{0x21,0xBB,0x0C});
            var charge = link.ReadReply(frame => CloudAlphaFrame(frame,0x0C) && frame[3] <= 2,550);
            if (charge != null) battery.Charging = charge[3] == 1;
            return battery;
        }

        static UsbBatteryDevice Cloud3(ushort product, string name, bool wireless = true) {
            return new UsbBatteryDevice(0x03F0,product,"headset",name,wireless,
                info => ExpandedUsbBattery.Page(info,0xFF13) && info.Usage == 1 && info.InputLength >= 5 &&
                    (info.OutputLength >= 62 || info.FeatureLength == 62),ReadCloud3);
        }

        internal static BatteryState ParseCloud3(byte[] frame) {
            if (frame == null || frame.Length < 5 || frame[0] != 0x66 ||
                (frame[1] != 0x89 && frame[1] != 0x0D) || (frame[2] == 0 && frame[3] == 0) || frame[4] > 100) return null;
            return ExpandedUsbBattery.Level(frame[4]);
        }

        static bool SendCloud3(HidLink link, byte command) {
            if (link.Info.OutputLength >= 62 && HidNative.HasReport(link.Handle,1,0x66,link.Info.OutputLength)) {
                try { link.Write(new byte[]{0x66,command}); return true; }
                catch (Win32Exception) {} catch (TimeoutException) {}
            }
            var request = new byte[62];
            request[0] = 0x66; request[1] = command;
            return link.WriteFeature(request);
        }

        static BatteryState ReadCloud3(HidLink link) {
            if (!HidNative.HasReport(link.Handle,0,0x66,link.Info.InputLength) || !SendCloud3(link,0x89)) return null;
            var battery = ExpandedUsbBattery.Listen(link,ParseCloud3,1000);
            if (battery == null || !SendCloud3(link,0x8A)) return battery;
            var charge = link.ReadReply(frame => frame.Length >= 3 && frame[0] == 0x66 &&
                (frame[1] == 0x8A || frame[1] == 0x0C) && frame[2] <= 2,550);
            if (charge != null) battery.Charging = charge[2] == 1;
            return battery;
        }

        internal static BatteryState ParseAula(byte[] frame) {
            if (frame == null || frame.Length < 8 || frame[0] != 0xAB || frame[1] != 0x47 || frame[2] != 0x4D ||
                frame[3] != 0x86 || frame[6] > 100 || (frame[6] == 0 && frame[7] == 0)) return null;
            return ExpandedUsbBattery.Level(frame[6],frame[7] == 2);
        }
    }
}
