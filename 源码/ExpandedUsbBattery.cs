using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery {
    internal sealed class UsbBatteryDevice {
        public readonly ushort Vendor, Product;
        public readonly string Kind, Name;
        public readonly bool Wireless, ListenOnly;
        readonly Func<HidInfo, bool> channel;
        internal readonly Func<HidLink, BatteryState> ReadPower;
        internal Func<HidLink, IEnumerable<HidInfo>, BatteryState> ReadGroup;
        internal Func<HidLink, MouseDpiState> ReadMouseDpi;

        public UsbBatteryDevice(ushort vendor, ushort product, string kind, string name, bool wireless,
                Func<HidInfo, bool> channel, Func<HidLink, BatteryState> readPower, bool listenOnly = false) {
            Vendor = vendor; Product = product; Kind = kind; Name = name; Wireless = wireless;
            this.channel = channel; ReadPower = readPower; ListenOnly = listenOnly;
        }

        internal bool Matches(HidInfo info) {
            return info.Transport == "usb" && info.Vid == Vendor && info.Pid == Product && channel(info);
        }
    }

    internal static class ExpandedUsbBattery {
        static readonly UsbBatteryDevice[] Models = ExpandedMouseBattery.Devices().Concat(ExpandedKeyboardBattery.Devices())
            .Concat(ExpandedHeadsetBattery.Devices()).ToArray();

        internal static UsbBatteryDevice Find(ushort vendor, ushort product) {
            return Models.FirstOrDefault(model => model.Vendor == vendor && model.Product == product);
        }

        internal static UsbBatteryDevice Match(ushort vendor, ushort product, IEnumerable<HidInfo> interfaces) {
            return Models.FirstOrDefault(model => model.Vendor == vendor && model.Product == product &&
                interfaces.Any(model.Matches));
        }

        internal static bool IsInputChannel(HidInfo info, string kind) {
            return Models.Any(model => model.Kind == kind && model.Matches(info));
        }

        internal static bool SupportsMouseDpi(string name) {
            return Models.Any(model => model.ReadMouseDpi != null &&
                String.Equals(model.Name,name,StringComparison.OrdinalIgnoreCase));
        }

        internal static bool TryReadMouseDpi(IEnumerable<HidInfo> devices, string key, out MouseDpiState state) {
            state = null;
            if (string.IsNullOrEmpty(key)) return false;
            var matches = (from model in Models
                           where model.ReadMouseDpi != null
                           from info in devices
                           where model.Matches(info) && BatteryReader.UsbDeviceKey(info,"mouse") == key
                           select new { Model=model, Info=info }).ToArray();
            if (matches.Length == 0) return false;
            if (matches.Length == 1) {
                var match = matches[0];
                state = HidLink.Use(match.Info,"mouse",match.Model.ReadMouseDpi,match.Model.ListenOnly);
                if (state != null) state.CanSwitch = false;
            }
            return true;
        }

        internal static BatteryState Read(UsbBatteryDevice model, IEnumerable<HidInfo> devices) {
            foreach (var info in devices.Where(model.Matches)) {
                var state = HidLink.Use(info, model.Kind,
                    link => model.ReadGroup == null ? model.ReadPower(link) : model.ReadGroup(link, devices), model.ListenOnly);
                if (state != null) return state;
            }
            return null;
        }

        internal static bool OnInterface(HidInfo info, int index) {
            return (info.Path ?? "").IndexOf("&mi_" + index.ToString("x2"), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool VendorPage(HidInfo info) { return info.UsagePage >= 0xFF00; }

        internal static bool Page(HidInfo info, ushort page) {
            return info.UsagePage == page || info.UsagePage == ((page << 8) & 0xFF00) + (page >> 8);
        }

        internal static BatteryState Level(int percent, bool? charging = null, bool estimated = false) {
            return new BatteryState { Percent=percent, Charging=charging, Estimated=estimated,
                Detail=estimated ? "估算电量" : "设备报告的电量" };
        }

        internal static BatteryState Listen(HidLink link, Func<byte[], BatteryState> decode, int timeout) {
            BatteryState reading = null;
            link.ReadReply(frame => (reading = decode(frame)) != null, timeout);
            return reading;
        }

        internal static BatteryState Ask(HidLink link, byte[] request, Func<byte[], BatteryState> decode,
                int timeout, byte? inputReport = null) {
            if (request.Length > link.Info.OutputLength ||
                !HidNative.HasReport(link.Handle, 1, request[0], link.Info.OutputLength) ||
                (inputReport.HasValue && !HidNative.HasReport(link.Handle, 0, inputReport.Value, link.Info.InputLength)))
                return null;
            link.Write(request);
            return Listen(link, decode, timeout);
        }
    }
}
