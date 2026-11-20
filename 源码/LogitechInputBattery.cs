using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace PeripheralBattery {
    internal sealed partial class LogitechInputBattery {
        static readonly ushort[] ReceiverPids = {
            0xC518, 0xC51A, 0xC51B, 0xC521, 0xC525, 0xC526, 0xC52B, 0xC52E, 0xC52F,
            0xC531, 0xC532, 0xC534, 0xC535, 0xC537, 0xC539, 0xC53A, 0xC53D, 0xC53F,
            0xC541, 0xC542, 0xC545, 0xC547, 0xC548, 0xC54D
        };

        readonly HidLink link, shortLink;
        readonly int firstClientId;
        int requestCount;

        LogitechInputBattery(HidLink link, HidLink shortLink, string kind, int clientId = 0) {
            this.link = link;
            this.shortLink = shortLink;
            // The two taskbar processes can query the same receiver.
            firstClientId = clientId == 0 ? (kind == "keyboard" ? 6 : 2) : clientId;
        }

        public static bool IsChannel(HidInfo info) {
            return info.Vid == 0x046D && info.UsagePage == 0xFF00 &&
                ((info.Usage == 2 && info.InputLength == 20 && info.OutputLength == 20) ||
                 (info.Usage == 1 && info.InputLength == 7 && info.OutputLength == 7));
        }

        public static bool IsReceiver(HidInfo info) {
            return info.Transport != "bluetooth" && (ReceiverPids.Contains(info.Pid) ||
                (info.Product ?? "").IndexOf("receiver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (info.Product ?? "").IndexOf("dongle", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static IEnumerable<BatteryState> Read(IList<HidInfo> group, string kind, string preferredKey) {
            var channels = group.Where(IsChannel).OrderByDescending(info => info.InputLength).ToList();
            if (channels.Count == 0) yield break;
            HidInfo primary = channels[0];
            bool receiver = IsReceiver(primary);
            byte[] slots = receiver ? new byte[] {1, 2, 3, 4, 5, 6} : new byte[] {0xFF};
            var readings = new List<BatteryState>();
            try {
                using (var link = new HidLink(primary)) {
                    // Some long requests respond through the companion short-report interface.
                    HidLink shortLink = null;
                    var shortInfo = channels.FirstOrDefault(info => info.InputLength == 7);
                    if (primary.InputLength == 20 && shortInfo != null) {
                        try { shortLink = new HidLink(shortInfo); }
                        catch (System.ComponentModel.Win32Exception) {}
                    }
                    using (shortLink) {
                        var reader = new LogitechInputBattery(link, shortLink, kind);
                        foreach (byte slot in slots) {
                            BatteryState reading = reader.ReadSlot(slot, kind, receiver);
                            if (reading == null) continue;
                            readings.Add(reading);
                            if (reading.Percent.HasValue && (preferredKey == null || reading.DeviceKey == preferredKey)) break;
                        }
                    }
                }
            } catch (System.ComponentModel.Win32Exception) {
                BatteryReader.Log("Logitech battery channel unavailable");
            } catch (TimeoutException) {
                BatteryReader.Log("Logitech battery query timed out");
            }
            foreach (var reading in readings) yield return reading;
        }

        BatteryState IdentifySlot(byte slot, string kind, bool receiver) {
            var ping = Request(slot, 0, 1, slot == 1 || !receiver ? 1000 : 300, 0, 0, 0xA5);
            if (ping == null || ping.Length < 3 || ping[0] < 2 || ping[2] != 0xA5) return null;

            byte nameFeature = Feature(slot, 0x0005);
            if (nameFeature == 0) return null;
            var type = Request(slot, nameFeature, 2);
            if (type == null || type.Length == 0 || KindForType(type[0]) != kind) return null;
            string name = ReadName(slot, nameFeature);
            if (String.IsNullOrWhiteSpace(name)) return null;

            string identity = link.Info.PhysicalId + "|" + slot + "|" + name;
            byte unitFeature = Feature(slot, 0x0003);
            if (unitFeature != 0) {
                var unit = Request(slot, unitFeature, 0);
                if (unit != null && unit.Length >= 5 && unit.Skip(1).Take(4).Any(value => value != 0) &&
                    unit.Skip(1).Take(4).Any(value => value != 0xFF))
                    identity = name + "|" + BitConverter.ToString(unit, 1, 4);
            }
            return new BatteryState {
                Kind = kind, Name = name, Connection = receiver ? "wireless" : "usb",
                DeviceKey = WindowsDevices.PrivateKey("logitech|" + kind + "|" + identity),
                Detail = "设备未返回电量百分比"
            };
        }

        BatteryState ReadSlot(byte slot, string kind, bool receiver) {
            var reading = IdentifySlot(slot, kind, receiver);
            if (reading == null) return receiver ? ReadLegacySlot(slot,kind) : null;
            byte batteryFeature = Feature(slot, 0x1004);
            if (batteryFeature != 0) {
                var capabilities = Request(slot, batteryFeature, 0);
                var status = Request(slot, batteryFeature, 1);
                ReadUnified(status,capabilities,reading);
            }
            if (!reading.Percent.HasValue) {
                batteryFeature = Feature(slot, 0x1000);
                if (batteryFeature != 0) ReadLevel(Request(slot, batteryFeature, 0), reading);
            }
            if (!reading.Percent.HasValue) {
                batteryFeature = Feature(slot,0x1001);
                if (batteryFeature != 0) ReadVoltage(Request(slot,batteryFeature,0),reading);
            }
            if (reading.Percent.HasValue) reading.Detail = reading.Estimated ? "估算电量" : "设备报告的电量";
            reading.CheckedAt = DateTime.Now;
            return reading;
        }

        static string KindForType(byte type) {
            if (type == 0 || type == 2) return "keyboard";
            if (type == 3 || type == 5) return "mouse";
            return null;
        }

        internal static void ReadUnified(byte[] status, byte[] capabilities, BatteryState reading) {
            if (status == null || status.Length < 3 || status[2] > 7) return;
            reading.Charging = status[2] == 1 || status[2] == 2 || status[2] == 4;
            if (capabilities != null && capabilities.Length >= 2 && (capabilities[1]&2) != 0 && status[0] <= 100) {
                reading.Percent = status[0];
                return;
            }
            var percent = ApproximateLevel(status[1]);
            if (percent.HasValue) {
                reading.Percent = percent;
                reading.Estimated = true;
            }
        }

        static int? ApproximateLevel(int flags) {
            switch (flags) {
                case 8: return 90;
                case 4: return 50;
                case 2: return 20;
                case 1: return 5;
                default: return null;
            }
        }

        static void ReadLevel(byte[] data, BatteryState reading) {
            if (data == null || data.Length < 3 || data[0] > 100) return;
            reading.Charging = data[2] == 1 || data[2] == 2 || data[2] == 4;
            if (data[2] == 0) reading.Percent = data[0];
            if (data[2] == 3) reading.Percent = 100;
            if (reading.Charging == true && data[0] != 0) reading.Percent = data[0];
        }

        static readonly int[] BatteryMillivolts = {3500,3579,3646,3671,3717,3751,3778,3811,3859,3922,3989,4067,4186};
        static readonly int[] BatteryPercentages = {0,2,5,10,20,30,40,50,60,70,80,90,100};

        internal static void ReadVoltage(byte[] data, BatteryState reading) {
            if (data == null || data.Length < 3) return;
            int voltage = (data[0]<<8)|data[1];
            if (voltage < 3500 || voltage > 4400) return;
            reading.Charging = (data[2]&0x80) != 0 && (data[2]&3) != 1;
            if (reading.Charging == true) return;
            int upper = Array.FindIndex(BatteryMillivolts,value => value >= voltage);
            if (upper < 0) reading.Percent = 100;
            else if (upper == 0) reading.Percent = 0;
            else {
                int span = BatteryMillivolts[upper]-BatteryMillivolts[upper-1];
                reading.Percent = BatteryPercentages[upper-1]+(voltage-BatteryMillivolts[upper-1])*
                    (BatteryPercentages[upper]-BatteryPercentages[upper-1])/span;
            }
            reading.Estimated = true;
        }

        BatteryState ReadLegacySlot(byte slot, string kind) {
            var writer = link.Info.OutputLength == 7 ? link : shortLink;
            if (writer == null) return null;
            byte pairing = (byte)(0x20+slot-1);
            var slotInfo = LegacyRegister(writer,0xFF,0xB5,pairing);
            if (slotInfo == null || slotInfo.Length < 8 || slotInfo[0] != pairing ||
                (slotInfo[7]&0x0F) != (kind == "mouse" ? 2 : 1)) return null;
            byte nameIndex = (byte)(0x40+slot-1);
            string name = ParseLegacyName(LegacyRegister(writer,0xFF,0xB5,nameIndex),nameIndex);
            if (name == null) return null;
            var percent = ParseLegacyBattery(LegacyRegister(writer,slot,0x0D),0x0D) ??
                ParseLegacyBattery(LegacyRegister(writer,slot,7),7);
            if (percent == null) return null;
            percent.Kind = kind;
            percent.Name = name;
            percent.Connection = "wireless";
            percent.DeviceKey = WindowsDevices.PrivateKey("logitech-legacy|"+kind+"|"+link.Info.PhysicalId+
                "|"+slot+"|"+name+"|"+BitConverter.ToString(slotInfo,3,2));
            return percent;
        }

        byte[] LegacyRegister(HidLink writer, byte slot, byte register, byte? parameter = null) {
            byte sub = parameter.HasValue ? (byte)0x83 : (byte)0x81;
            return Exchange(writer,slot,sub,register,parameter.HasValue ? new[]{parameter.Value} : new byte[0],350);
        }

        internal static string ParseLegacyName(byte[] frame, byte parameter) {
            if (frame == null || frame.Length < 3 || frame[0] != parameter || frame[1] == 0 ||
                frame[1] > frame.Length-2 || frame.Skip(2).Take(frame[1]).Any(value => value < 32 || value > 126))
                return null;
            string name = Encoding.ASCII.GetString(frame,2,frame[1]).Trim();
            return name.Length == 0 ? null : name;
        }

        internal static BatteryState ParseLegacyBattery(byte[] data, byte register) {
            if (data == null || data.Length < 3) return null;
            if (register == 0x0D) {
                int state = data[2]&0xF0;
                return data[0] <= 100 && (state == 0x30 || state == 0x50 || state == 0x90)
                    ? ExpandedUsbBattery.Level(data[0],state == 0x50) : null;
            }
            int flags;
            switch (data[0]) {
                case 7: flags=8; break;
                case 5: flags=4; break;
                case 3: flags=2; break;
                case 1: flags=1; break;
                default: return null;
            }
            bool? charging = data[1] == 0 || (data[1]&0x22) == 0x22 ? (bool?)false :
                (data[1]&0x21) == 0x21 ? (bool?)true : null;
            return ExpandedUsbBattery.Level(ApproximateLevel(flags).Value,charging,true);
        }

        string ReadName(byte slot, byte feature) {
            var length = Request(slot, feature, 0);
            if (length == null || length.Length == 0 || length[0] == 0 || length[0] > 80) return null;
            var name = new List<byte>();
            while (name.Count < length[0]) {
                var part = Request(slot, feature, 1, 600, (byte)name.Count);
                if (part == null || part.Length == 0) return null;
                name.AddRange(part.Take(length[0] - name.Count));
            }
            return Encoding.UTF8.GetString(name.ToArray()).TrimEnd('\0').Trim();
        }

        byte Feature(byte slot, ushort feature) {
            var response = Request(slot, 0, 0, 600, (byte)(feature >> 8), (byte)feature, 0);
            return response == null ? (byte)0 : response[0];
        }

        byte[] Request(byte slot, byte feature, byte function, int timeout = 600, params byte[] parameters) {
            byte command = (byte)((function << 4) | (firstClientId + requestCount++ % 4));
            return Exchange(link,slot,feature,command,parameters,timeout);
        }

        byte[] Exchange(HidLink writer, byte slot, byte feature, byte command, byte[] parameters, int timeout) {
            var report = new byte[writer.Info.OutputLength];
            report[0] = writer.Info.OutputLength == 20 ? (byte)0x11 : (byte)0x10;
            report[1] = slot;
            report[2] = feature;
            report[3] = command;
            Array.Copy(parameters, 0, report, 4, Math.Min(parameters.Length, report.Length - 4));
            writer.Write(report);
            var timer = Stopwatch.StartNew();
            var channels = new[] {link, shortLink};
            while (timer.ElapsedMilliseconds < timeout) {
                foreach (var channel in channels) {
                    if (channel == null) continue;
                    var reply = channel.Read(10);
                    if (reply == null || reply.Length < 7 || (reply[0] != 0x10 && reply[0] != 0x11) || reply[1] != slot)
                        continue;
                    if (reply[2] == feature && reply[3] == command)
                        return reply.Skip(4).Take(reply[0] == 0x10 ? 3 : 16).ToArray();
                    if ((reply[2] == 0x8F || reply[2] == 0xFF) && reply[3] == feature && reply[4] == command)
                    {
                        if (firstClientId == 10) BatteryReader.Log("DPI request rejected: command "+command+" code "+reply[5]);
                        return null;
                    }
                }
            }
            return null;
        }
    }
}
