using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PeripheralBattery {
    public sealed class BatteryState {
        public string Kind, Name, Detail, Connection, DeviceKey;
        public int? Percent;
        public bool? Charging;
        public bool Estimated;
        public DateTime CheckedAt = DateTime.Now;
    }

    public static class BatteryReader {
        public static Action<string> Trace;
        internal static void Log(string line) { if (Trace != null) Trace(line); }

        public static List<BatteryState> ReadAll(string activeKind = null) {
            var devices = HidNative.Enumerate();
            return new[] {"mouse", "keyboard"}.Select(kind =>
                activeKind == null || activeKind == kind ?
                Select(kind, ReadUsb(kind, UsbGroups(devices, kind), null)
                    .Concat(BluetoothInputBattery.Read(devices, kind)), null) : Missing(kind)).ToList();
        }

        internal static BatteryState Missing(string kind) {
            string label = kind == "mouse" ? "鼠标" : "键盘";
            return new BatteryState {Kind = kind, Name = label, Detail = "未检测到外接" + label};
        }

        internal static bool HasUsage(HidInfo info, string kind) {
            return info.UsagePage == 1 && (kind == "mouse" ? info.Usage == 2 : info.Usage == 6 || info.Usage == 7);
        }

        internal static List<IGrouping<string, HidInfo>> UsbGroups(IEnumerable<HidInfo> devices, string kind) {
            return devices.Where(info => info.Transport == "usb")
                .GroupBy(info => info.PhysicalId, StringComparer.OrdinalIgnoreCase)
                .Where(group => !group.Any(info => UsbHeadsetSupport.Find(info.Vid, info.Pid) != null))
                .Where(group => group.Any(info => HasUsage(info, kind) || LogitechInputBattery.IsChannel(info) ||
                    ExpandedUsbBattery.IsInputChannel(info, kind) ||
                    (kind == "mouse" && AsusAimPointMouse.IsChannel(info)) ||
                    (kind == "mouse" && UsbMouseSupport.IsChannel(info, UsbMouseSupport.Find(info.Vid, info.Pid))) ||
                    (kind == "keyboard" && (KeyboardProtocol.IsChannel(info) || UsbKeyboardSupport.IsChannel(info)))))
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static IEnumerable<BatteryState> ReadUsb(string kind, IEnumerable<IGrouping<string, HidInfo>> groups, string preferredKey) {
            foreach (var group in groups) {
                var first = group.First();
                var expanded = ExpandedUsbBattery.Match(first.Vid, first.Pid, group);
                if (expanded != null && expanded.Kind != "headset") {
                    if (kind == expanded.Kind) {
                        var reading = ExpandedUsbBattery.Read(expanded, group) ?? new BatteryState { Detail = "设备暂未返回电量" };
                        reading.Kind = kind;
                        reading.Name = reading.Name ?? expanded.Name ?? first.Product ?? "无线设备";
                        reading.Connection = expanded.Wireless ? "wireless" : "usb";
                        reading.DeviceKey = reading.DeviceKey ?? UsbDeviceKey(first, kind);
                        if (!reading.Percent.HasValue) ReadStandardUsbBattery(reading, group, first, kind);
                        yield return reading;
                    }
                    continue;
                }
                if (group.Any(UsbKeyboardSupport.IsChannel)) {
                    if (kind == "keyboard") {
                        var keyboard = UsbKeyboardSupport.Read(group);
                        if (keyboard != null) {
                            if (!keyboard.Percent.HasValue) ReadStandardUsbBattery(keyboard, group, first, kind);
                            yield return keyboard;
                        }
                    }
                    continue;
                }
                var model = UsbMouseSupport.Find(first.Vid, first.Pid);
                if (model != null && group.Any(info => UsbMouseSupport.IsChannel(info, model))) {
                    if (kind == "mouse") {
                        string mouseName = model.RequireSignature ? first.Product ?? "无线鼠标" : model.Name;
                        var reading = UsbState(first, kind, mouseName, model.Receiver ? "wireless" : "usb", "设备暂未返回电量");
                        UsbMouseSupport.ReadBattery(group, model, reading);
                        if (!reading.Percent.HasValue) ReadStandardUsbBattery(reading, group, first,
                            model.RequireSignature && reading.Name != model.Name ? KindInName(reading.Name) : kind);
                        yield return reading;
                    }
                    continue;
                }
                var aimPoint = group.FirstOrDefault(info => AsusAimPointMouse.IsProduct(info.Vid, info.Pid));
                if (aimPoint != null) {
                    if (kind == "mouse") {
                        var channel = group.FirstOrDefault(AsusAimPointMouse.IsChannel);
                        var reading = UsbState(aimPoint, kind, AsusAimPointMouse.Name,
                            AsusAimPointMouse.Connection(aimPoint), "暂未收到鼠标电量");
                        if (channel != null) AsusAimPointMouse.ReadPower(channel, reading);
                        yield return reading;
                    }
                    continue;
                }
                var mchose = group.FirstOrDefault(KeyboardProtocol.IsChannel);
                if (mchose != null) {
                    // This keyboard also exposes a mouse collection for macros.
                    if (kind == "keyboard") yield return ReadKeyboard(mchose);
                    continue;
                }
                var logitech = group.FirstOrDefault(LogitechInputBattery.IsChannel);
                if (logitech != null) {
                    var readings = LogitechInputBattery.Read(group.ToList(), kind, preferredKey).ToList();
                    foreach (var reading in readings) {
                        if (!reading.Percent.HasValue && reading.Connection == "usb" &&
                                IsKnownWiredInput(logitech.Vid,reading.Name,logitech.Pid)) reading.Detail = "有线供电";
                        yield return reading;
                    }
                    if (readings.Count != 0) continue;
                    if (LogitechInputBattery.IsReceiver(logitech)) {
                        if (logitech.Pid == 0xC547 && kind == "mouse") {
                            yield return UsbState(logitech, kind, "罗技 LIGHTSPEED 接收器", "wireless", "接收器已连接，配对鼠标暂未响应");
                        }
                        // A Logitech receiver can expose both usages even with only one device paired.
                        continue;
                    }
                }
                var input = group.FirstOrDefault(info => HasUsage(info, kind));
                if (input == null) continue;
                string name = group.Select(info => info.Product).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value));
                string namedKind = KindInName(name);
                if (namedKind != null && namedKind != kind && group.Any(info => HasUsage(info, namedKind))) continue;
                string connection = IsReceiverName(name) ? "wireless" : "usb";
                var fallback = UsbState(input, kind, name ?? (kind == "mouse" ? "USB 鼠标" : "USB 键盘"),
                    connection, "设备已识别，暂无可读电量");
                ReadStandardUsbBattery(fallback, group, input, namedKind);
                if (!fallback.Percent.HasValue && connection == "usb" && IsKnownWiredInput(input.Vid,name,input.Pid))
                    fallback.Detail = "有线供电";
                yield return fallback;
            }
        }

        internal static bool IsKnownWiredInput(ushort vendor, string name, ushort product = 0) {
            var model = (name ?? "").ToUpperInvariant();
            switch (vendor) {
                case 0x046D: return product == 0xC099 || model.Contains("G502 HERO") || model.Contains("G502 SE HERO") ||
                    model.Contains("G512") || model.Contains("G316 X 75");
                case 0x1532: return model.Contains("BLACKWIDOW V4 X") || model.Contains("HUNTSMAN V3 PRO");
                case 0x03F0: return model.Contains("ORIGINS 2 PRO 65") || model.Contains("ORIGINS 2 65") ||
                    model == "HYPERX PULSEFIRE HASTE 2" || model == "PULSEFIRE HASTE 2";
                case 0x1038: return product == 0x1850;
                case 0x2516: return product == 0x0101 || model.Contains("MM711");
                default: return false;
            }
        }

        static void ReadStandardUsbBattery(BatteryState state, IEnumerable<HidInfo> group, HidInfo input, string namedKind) {
            // A combo receiver can represent two batteries.
            bool standalone = namedKind == state.Kind ||
                !group.Any(info => HasUsage(info, state.Kind == "mouse" ? "keyboard" : "mouse"));
            var channels = standalone ? group : group.Where(info => HasUsage(info, state.Kind));
            var nodes = channels.Select(info => info.Node);
            if (standalone) nodes = new[]{input.PhysicalNode}.Concat(nodes);
            state.Percent = HidBattery.Read(nodes.Distinct(), channels);
            if (state.Percent.HasValue) state.Detail = "设备标准电量";
        }

        static string KindInName(string name) {
            if (String.IsNullOrEmpty(name)) return null;
            bool mouse = name.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("鼠标");
            bool keyboard = name.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("键盘");
            if (mouse == keyboard) return null;
            return mouse ? "mouse" : "keyboard";
        }

        static bool IsReceiverName(string name) {
            return name != null && (name.IndexOf("receiver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("dongle", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("接收器"));
        }

        internal static string UsbDeviceKey(HidInfo info, string kind) {
            string identity = info.Container == Guid.Empty ? info.PhysicalId : info.Container.ToString("N");
            return WindowsDevices.PrivateKey("usb|" + kind + "|" + identity);
        }

        internal static BatteryState UsbState(HidInfo info, string kind, string name, string connection, string detail) {
            return new BatteryState {
                Kind = kind, Name = name, Connection = connection, Detail = detail,
                DeviceKey = UsbDeviceKey(info, kind)
            };
        }

        internal static BatteryState Select(string kind, IEnumerable<BatteryState> readings, string preferredKey) {
            return readings.OrderByDescending(state => state.Percent.HasValue)
                .ThenByDescending(state => state.Charging == true)
                .ThenByDescending(state => state.DeviceKey == preferredKey)
                .ThenBy(state => state.Connection == "usb" ? 0 : 1)
                .ThenBy(state => state.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(state => state.DeviceKey, StringComparer.Ordinal)
                .FirstOrDefault() ?? Missing(kind);
        }

        static byte[] QueryKeyboard(HidLink link, byte command, ushort offset, BatteryState state) {
            link.Write(KeyboardProtocol.BuildRead(command, offset, link.Info.OutputLength));
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 1500) {
                var raw = link.Read(100);
                if (raw == null) continue;
                int percent;
                bool charging;
                if (KeyboardProtocol.TryBatteryEvent(raw, out percent, out charging)) {
                    state.Percent = percent;
                    state.Charging = charging;
                    state.Detail = "键盘实时电量广播";
                    return null;
                }
                byte[] payload;
                if (KeyboardProtocol.TryResponse(raw, command, offset, out payload)) return payload;
                if (KeyboardProtocol.IsRejected(raw, command)) return null;
            }
            return null;
        }

        static BatteryState ReadKeyboard(HidInfo info) {
            var reading = UsbState(info, "keyboard", info.Product, info.Pid == 0x2015 ? "usb" : "wireless",
                "键盘暂未响应");
            try {
                using (var link = new HidLink(info)) {
                    var profiles = QueryKeyboard(link, 4, 0, reading);
                    if (!reading.Percent.HasValue) {
                        var config = QueryKeyboard(link, 5, (ushort)(KeyboardProtocol.Profile(profiles) * 64), reading);
                        int percent;
                        bool charging;
                        if (KeyboardProtocol.TryPower(config, out percent, out charging)) {
                            reading.Percent = percent;
                            reading.Charging = charging;
                            reading.Detail = "键盘实时电量与充电状态";
                        }
                    }
                }
            } catch (System.ComponentModel.Win32Exception) {
                Log("MCHOSE battery channel unavailable");
            } catch (TimeoutException) {
                Log("MCHOSE battery query timed out");
            }
            reading.CheckedAt = DateTime.Now;
            return reading;
        }
    }

    internal sealed class InputBatteryMonitor {
        readonly string kind;
        readonly TimeSpan batteryInterval = TimeSpan.FromMinutes(2);
        DateTime nextUsbRead;
        string[] usbInterfaces = new string[0];
        List<BatteryState> usbReadings = new List<BatteryState>();
        BatteryState current;

        public InputBatteryMonitor(string kind) { this.kind = kind; }

        public BatteryState Read(bool force) {
            var devices = HidNative.Enumerate();
            var groups = BatteryReader.UsbGroups(devices, kind);
            var interfaces = groups.SelectMany(group => group.Select(info => info.Path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            string preferredKey = current == null ? null : current.DeviceKey;
            if (force || DateTime.UtcNow >= nextUsbRead ||
                !usbInterfaces.SequenceEqual(interfaces, StringComparer.OrdinalIgnoreCase)) {
                usbReadings = BatteryReader.ReadUsb(kind, groups, preferredKey).ToList();
                usbInterfaces = interfaces;
                nextUsbRead = DateTime.UtcNow.Add(batteryInterval);
            }
            var selected = BatteryReader.Select(kind, usbReadings.Concat(BluetoothInputBattery.Read(devices, kind, force)), preferredKey);
            if (current == null || current.DeviceKey != null || selected.DeviceKey != null) current = selected;
            return current;
        }
    }
}
