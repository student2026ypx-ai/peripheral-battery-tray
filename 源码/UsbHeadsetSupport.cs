using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace PeripheralBattery {
    internal enum HeadsetProtocol { LogitechG533, LogitechG535, LogitechG733, RazerLegacy, Xiberia, Edifier, Mchose, LogitechGProX }

    internal sealed class UsbHeadsetModel {
        public readonly ushort Vendor, Product;
        public readonly string Name;
        public readonly HeadsetProtocol Protocol;
        internal readonly UsbBatteryDevice Expanded;
        public UsbHeadsetModel(ushort vendor, ushort product, string name, HeadsetProtocol protocol) {
            Vendor = vendor; Product = product; Name = name; Protocol = protocol;
        }
        internal UsbHeadsetModel(UsbBatteryDevice expanded) {
            Vendor=expanded.Vendor; Product=expanded.Product; Name=expanded.Name; Expanded=expanded;
        }
    }

    internal static class UsbHeadsetSupport {
        static readonly Regex UsbIds = new Regex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?=[&\\])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly UsbHeadsetModel[] Models = {
            Logitech(0x0A66, "G533", HeadsetProtocol.LogitechG533),
            Logitech(0x0AC4, "G535", HeadsetProtocol.LogitechG535),
            Logitech(0x0AB5, "G733", HeadsetProtocol.LogitechG733),
            Logitech(0x0AFE, "G733", HeadsetProtocol.LogitechG733),
            Logitech(0x0B1F, "G733", HeadsetProtocol.LogitechG733),
            Logitech(0x0A5B, "G933", HeadsetProtocol.LogitechG733),
            Logitech(0x0A87, "G935", HeadsetProtocol.LogitechG733),
            Logitech(0x0ABA, "G PRO X Wireless", HeadsetProtocol.LogitechGProX),
            new UsbHeadsetModel(0x1532, 0x0528, "Razer BlackShark V2 Pro (2020)", HeadsetProtocol.RazerLegacy),
            new UsbHeadsetModel(0x1532, 0x051C, "Razer Nari", HeadsetProtocol.RazerLegacy),
            new UsbHeadsetModel(0x36F9, 0xF008, "西伯利亚 T7", HeadsetProtocol.Xiberia),
            new UsbHeadsetModel(0x2D99, 0xE049, "漫步者 花再 Halo Space", HeadsetProtocol.Edifier),
            new UsbHeadsetModel(0x41E4, 0x3201, "MCHOSE V9", HeadsetProtocol.Mchose),
            new UsbHeadsetModel(0x41E4, 0x3202, "MCHOSE V9 Pro", HeadsetProtocol.Mchose),
            new UsbHeadsetModel(0x41E4, 0x3502, "MCHOSE G9 Pro", HeadsetProtocol.Mchose),
            new UsbHeadsetModel(0x41E4, 0x3402, "MCHOSE S9 Pro", HeadsetProtocol.Mchose),
            new UsbHeadsetModel(0x3837, 0x6045, "MCHOSE X9", HeadsetProtocol.Mchose)
        };
        static readonly byte[] RazerCommand = {0xFF, 0x0A, 0, 0xFD, 4, 0x12, 0xF1, 2, 5};
        static readonly byte[] RazerAnswer = {0xFF, 0x0F, 5, 0xFE, 0x12, 4, 0x1F, 8, 5};
        static readonly int[] G533Volts = {4200,3850,3790,3750,3680,3330};
        static readonly int[] G535Volts = {4175,3817,3766,3730,3664,3310};
        static readonly int[] G733Volts = {4100,3950,3850,3750,3650,3500,3300,3150};
        static readonly int[] GProXVolts = {4150,3830,3780,3740,3670,3320};
        static readonly int[] G533Levels = {100,50,30,20,5,0};
        static readonly int[] G733Levels = {100,80,60,40,20,10,5,0};

        static UsbHeadsetModel Logitech(ushort product, string name, HeadsetProtocol protocol) {
            return new UsbHeadsetModel(0x046D, product, "Logitech " + name, protocol);
        }

        internal static UsbHeadsetModel Find(ushort vendor, ushort product) {
            var model = Models.FirstOrDefault(item => item.Vendor == vendor && item.Product == product);
            if (model != null) return model;
            var expanded = ExpandedUsbBattery.Find(vendor, product);
            return expanded != null && expanded.Kind == "headset" ? new UsbHeadsetModel(expanded) : null;
        }

        internal static UsbHeadsetModel Find(string instanceId) {
            var match = UsbIds.Match(instanceId ?? "");
            return !match.Success ? null : Find(
                UInt16.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                UInt16.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        internal static Dictionary<Guid, UsbHeadsetModel> ConnectedModels(IEnumerable<string> ids = null) {
            var found = new Dictionary<Guid, UsbHeadsetModel>();
            foreach (var id in ids ?? WindowsDevices.PresentIds("USB")) {
                var model = Find(id);
                uint node;
                if (model == null || !WindowsDevices.TryLocate(id, out node)) continue;
                var container = WindowsDevices.Container(node);
                if (container != Guid.Empty && container != WindowsDevices.SystemContainer && !found.ContainsKey(container))
                    found.Add(container, model);
            }
            return found;
        }

        internal static bool IsChannel(HidInfo info, UsbHeadsetModel model) {
            if (model == null || info.Transport != "usb" || info.Vid != model.Vendor || info.Pid != model.Product)
                return false;
            if (model.Expanded != null) return model.Expanded.Matches(info);
            switch (model.Protocol) {
                case HeadsetProtocol.LogitechG533:
                case HeadsetProtocol.LogitechG733:
                case HeadsetProtocol.LogitechGProX:
                    return info.UsagePage == 0xFF43 && info.Usage == 0x0202 && info.OutputLength == 20 && info.InputLength >= 7;
                case HeadsetProtocol.LogitechG535:
                    return info.UsagePage == 0x000C && info.Usage == 1 && info.OutputLength == 20 && info.InputLength >= 7;
                case HeadsetProtocol.RazerLegacy:
                    return info.UsagePage == 0xFF00 && info.Usage == 1 && info.FeatureLength == 64;
                case HeadsetProtocol.Xiberia:
                    return info.UsagePage == 0xFF00 && info.InputLength == 64 && info.OutputLength == 64;
                case HeadsetProtocol.Edifier:
                    return info.UsagePage == 0xFF14 && info.Usage == 1 && info.InputLength == 64 && info.OutputLength == 64;
                case HeadsetProtocol.Mchose:
                    return info.InputLength >= 4 && (info.OutputLength == 16 || info.OutputLength == 64);
                default: return false;
            }
        }

        internal static BatteryState Read(UsbHeadsetModel model, IEnumerable<HidInfo> channels) {
            if (model.Expanded != null) return ExpandedUsbBattery.Read(model.Expanded, channels);
            foreach (var channel in channels.Where(info => IsChannel(info, model))) {
                var state = HidLink.Use(channel, "headset", link => ReadProtocol(link, model.Protocol));
                if (state != null) return state;
            }
            return null;
        }

        static BatteryState ReadProtocol(HidLink link, HeadsetProtocol protocol) {
            switch (protocol) {
                case HeadsetProtocol.RazerLegacy:
                    var request = new byte[64];
                    RazerCommand.CopyTo(request, 0);
                    if (!link.WriteFeature(request)) return null;
                    for (int attempt = 0; attempt < 4; attempt++) {
                        Thread.Sleep(100);
                        var state = ParseRazer(link.ReadFeature(0xFF));
                        if (state != null) return state;
                    }
                    return null;
                case HeadsetProtocol.Xiberia:
                    return Query(link, new byte[]{0x55,0x65,0,0}, 0x55, ParseXiberia);
                case HeadsetProtocol.Edifier:
                    return Query(link, new byte[]{0x2E,0xAA,0xEC,0xD0,0,0,0x66}, 0x2F, ParseEdifier);
                case HeadsetProtocol.Mchose:
                    return Query(link, new byte[]{0x55,0x65,1}, 0x55, ParseMchose);
                case HeadsetProtocol.LogitechGProX:
                    return Query(link,new byte[]{0x11,0xFF,6,0x0D},null,
                        frame => ParseLogitech(frame,protocol,6,0x0D));
                default:
                    byte feature = protocol == HeadsetProtocol.LogitechG533 ? (byte)7 : protocol == HeadsetProtocol.LogitechG535 ? (byte)5 : (byte)8;
                    byte function = protocol == HeadsetProtocol.LogitechG533 ? (byte)1 : protocol == HeadsetProtocol.LogitechG535 ? (byte)0x0D : (byte)0x0A;
                    return Query(link, new byte[]{0x11,0xFF,feature,function}, null,
                        frame => ParseLogitech(frame, protocol, feature, function));
            }
        }

        static BatteryState Query(HidLink link, byte[] request, byte? inputReport, Func<byte[], BatteryState> parse) {
            if (!HidNative.HasReport(link.Handle, 1, request[0], link.Info.OutputLength) ||
                (inputReport.HasValue && !HidNative.HasReport(link.Handle, 0, inputReport.Value, link.Info.InputLength)))
                return null;
            link.Write(request);
            BatteryState state = null;
            link.ReadReply(frame => (state = parse(frame)) != null, 750);
            return state;
        }

        static BatteryState Power(int? percent, bool? charging = null, string detail = "耳机报告的电量") {
            return new BatteryState { Percent = percent, Charging = charging, Detail = detail };
        }

        internal static BatteryState ParseLogitech(byte[] packet, HeadsetProtocol protocol, byte feature, byte command) {
            if (packet == null || packet.Length < 7 || (packet[0] != 0x10 && packet[0] != 0x11) ||
                packet[1] != 0xFF || packet[2] != feature || packet[3] != command ||
                (packet[6] & 1) == 0) return null;
            int[] voltages = protocol == HeadsetProtocol.LogitechG533 ? G533Volts :
                protocol == HeadsetProtocol.LogitechG535 ? G535Volts :
                protocol == HeadsetProtocol.LogitechGProX ? GProXVolts : G733Volts;
            int[] levels = protocol == HeadsetProtocol.LogitechG733 ? G733Levels : G533Levels;
            int millivolts = (packet[4] << 8) | packet[5];
            if (millivolts < voltages[voltages.Length - 1] || millivolts > 5000) return null;
            int percent = 100;
            for (int i = 1; i < voltages.Length && millivolts < voltages[0]; i++) {
                if (millivolts < voltages[i]) continue;
                int gap = voltages[i - 1] - voltages[i];
                percent = levels[i] + ((millivolts - voltages[i]) * (levels[i - 1] - levels[i]) + gap / 2) / gap;
                break;
            }
            var reading = Power(percent, (packet[6] & 2) != 0, "按耳机电池电压估算");
            reading.Estimated = true;
            return reading;
        }

        internal static BatteryState ParseRazer(byte[] packet) {
            if (packet == null || packet.Length != 64 || !packet.Take(RazerAnswer.Length).SequenceEqual(RazerAnswer) ||
                (packet[12] == 0 && packet[13] == 0)) return null;
            if (packet[11] == 9) return Power(null, true, "耳机正在充电，暂无有效百分比");
            bool? charging = packet[11] == 1 || packet[11] == 2 || packet[11] == 3 || packet[11] == 6 ? (bool?)false : null;
            return packet[14] <= 100 ? Power(packet[14], charging) : null;
        }

        internal static BatteryState ParseXiberia(byte[] packet) {
            return packet != null && packet.Length >= 4 && packet[0] == 0x55 && packet[1] == 0x65 && packet[2] == 0 &&
                packet[3] > 0 && packet[3] <= 100 ? Power(packet[3]) : null;
        }

        internal static BatteryState ParseEdifier(byte[] packet) {
            if (packet == null || packet.Length < 8 || packet[0] != 0x2F || packet[1] != 0xBB ||
                packet[2] != 0xEC || packet[3] != 0xD0 || packet[4] != 0 || packet[5] != 1) return null;
            int percent = packet[6] ^ 0xA5;
            return (packet.Skip(1).Take(6).Sum(value => (int)value) & 255) == packet[7] &&
                percent <= 100 ? Power(percent) : null;
        }

        internal static BatteryState ParseMchose(byte[] packet) {
            if (packet == null || packet.Length < 4 || packet[0] != 0x55 ||
                packet[1] != 0x65 || packet[2] > 100 || packet[3] < 2 || packet[3] > 4) return null;
            return Power(packet[2], packet[3] == 3);
        }
    }
}
