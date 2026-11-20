using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    internal enum UsbMouseProtocol { Razer, Mchose, Dareu, Aula }

    internal sealed class UsbMousePower {
        public string Name;
        public int? Percent;
        public bool? Charging;
    }

    internal sealed class UsbMouseModel {
        public readonly ushort Vendor, Product;
        public readonly string Name;
        public readonly UsbMouseProtocol Protocol;
        public readonly bool Receiver;
        public bool RequireSignature { get { return Protocol == UsbMouseProtocol.Aula; } }
        public UsbMouseModel(ushort vendor, ushort product, string name, UsbMouseProtocol protocol, bool receiver) {
            Vendor = vendor; Product = product; Name = name; Protocol = protocol; Receiver = receiver;
        }
    }

    internal static class UsbMouseSupport {
        const string MchoseReceiver = "迈从鼠标接收器";
        static readonly UsbMouseModel[] Models = {
            Razer(0x00A5, "Viper V2 Pro", false), Razer(0x00A6, "Viper V2 Pro", true),
            Razer(0x00C0, "Viper V3 Pro", false), Razer(0x00C1, "Viper V3 Pro", true),
            Razer(0x00B6, "DeathAdder V3 Pro", false), Razer(0x00B7, "DeathAdder V3 Pro", true),
            Razer(0x00AA, "Basilisk V3 Pro", false), Razer(0x00AB, "Basilisk V3 Pro", true),
            Razer(0x00AF, "Cobra Pro", false), Razer(0x00B0, "Cobra Pro", true),
            Razer(0x00A7, "Naga V2 Pro", false), Razer(0x00A8, "Naga V2 Pro", true),
            Razer(0x0094, "Orochi V2", true), Razer(0x00E0, "Orochi V2", true),
            Razer(0x009C, "DeathAdder V2 X HyperSpeed", true),
            Razer(0x00B4, "Naga V2 HyperSpeed", true),
            Razer(0x00B8, "Viper V3 HyperSpeed", true),
            Razer(0x00B9, "Basilisk V3 X HyperSpeed", true),
            Razer(0x00BE, "DeathAdder V4 Pro", false), Razer(0x00BF, "DeathAdder V4 Pro", true),
            Razer(0x00C2, "DeathAdder V3 Pro", false), Razer(0x00C3, "DeathAdder V3 Pro", true),
            Razer(0x00C4, "DeathAdder V3 HyperSpeed", false), Razer(0x00C5, "DeathAdder V3 HyperSpeed", true),
            Razer(0x00C7, "Pro Click V2 Vertical Edition", false), Razer(0x00C8, "Pro Click V2 Vertical Edition", true),
            Razer(0x00CC, "Basilisk V3 Pro 35K", false), Razer(0x00CD, "Basilisk V3 Pro 35K", true),
            Razer(0x00D6, "Basilisk V3 Pro 35K", false), Razer(0x00D7, "Basilisk V3 Pro 35K", true),
            Razer(0x00D0, "Pro Click V2", false), Razer(0x00D1, "Pro Click V2", true),
            Razer(0x00D3, "Basilisk Mobile", false), Razer(0x00D4, "Basilisk Mobile", true),
            Razer(0x00DA, "Cobra HyperSpeed", false), Razer(0x00DB, "Cobra HyperSpeed", true),
            Razer(0x00DE, "Viper V3 Pro SE", false), Razer(0x00DF, "Viper V3 Pro SE", true),
            Razer(0x00E5, "Viper V4 Pro", false), Razer(0x00E6, "Viper V4 Pro", true),
            new UsbMouseModel(0x5253, 0x1020, MchoseReceiver, UsbMouseProtocol.Mchose, true),
            new UsbMouseModel(0x5253, 0x00B0, "MCHOSE L7 Pro", UsbMouseProtocol.Mchose, false),
            new UsbMouseModel(0x5253, 0x0031, "MCHOSE M7 Ultra", UsbMouseProtocol.Mchose, false),
            new UsbMouseModel(0x260D, 0x1114, "DAREU A950 PRO Mg", UsbMouseProtocol.Dareu, true),
            new UsbMouseModel(0x260D, 0x1117, "DAREU A950 PRO Mg", UsbMouseProtocol.Dareu, false),
            new UsbMouseModel(0x258A, 0x0138, "AULA SC380 Pro", UsbMouseProtocol.Aula, true)
        };

        static UsbMouseModel Razer(ushort product, string name, bool receiver) {
            return new UsbMouseModel(0x1532, product, "Razer " + name, UsbMouseProtocol.Razer, receiver);
        }

        internal static UsbMouseModel Find(ushort vendor, ushort product) {
            return Models.FirstOrDefault(model => model.Vendor == vendor && model.Product == product);
        }
        internal static bool SupportsDpi(string name) {
            return name != MchoseReceiver && Models.Any(model => String.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsChannel(HidInfo info, UsbMouseModel model) {
            if (model == null || info.Transport != "usb" || info.Vid != model.Vendor || info.Pid != model.Product)
                return false;
            switch (model.Protocol) {
                case UsbMouseProtocol.Razer:
                    return RazerMouse.IsChannel(info);
                case UsbMouseProtocol.Mchose:
                    return info.FeatureLength == 65 && info.UsagePage == 0xFF01 &&
                        (info.Path ?? "").IndexOf("&mi_02", StringComparison.OrdinalIgnoreCase) >= 0;
                case UsbMouseProtocol.Dareu:
                    return info.UsagePage == 0xFF02 && info.Usage == 2 && info.InputLength == 17 && info.OutputLength == 17;
                case UsbMouseProtocol.Aula:
                    return info.UsagePage == 0xFF02 && info.Usage == 2 && info.FeatureLength == 520;
                default:
                    return false;
            }
        }

        static IEnumerable<HidInfo> Channels(IEnumerable<HidInfo> group, UsbMouseModel model) {
            if (model.Protocol == UsbMouseProtocol.Dareu && model.Receiver &&
                !group.Any(info => info.UsagePage == 0xFF05 && info.Usage == 0)) return Enumerable.Empty<HidInfo>();
            return group.Where(info => IsChannel(info, model)).OrderByDescending(info => info.UsagePage >= 0xFF00);
        }

        internal static void ReadBattery(IEnumerable<HidInfo> group, UsbMouseModel model, BatteryState reading) {
            foreach (var info in Channels(group, model)) {
                var result = UseChannel(info, link => {
                    switch (model.Protocol) {
                        case UsbMouseProtocol.Razer: return RazerMouse.ReadPower(link);
                        case UsbMouseProtocol.Mchose: return MchoseMouse.ReadPower(link, model);
                        case UsbMouseProtocol.Dareu: return DareuMouse.ReadPower(link);
                        case UsbMouseProtocol.Aula: return AulaMouse.ReadPower(link);
                        default: return null;
                    }
                });
                if (result == null) continue;
                if (result.Name != null) {
                    reading.Name = result.Name;
                    reading.DeviceKey = MchoseMouse.PairedKey(info, result.Name);
                }
                if (model.RequireSignature) reading.Name = model.Name;
                reading.Percent = result.Percent;
                reading.Charging = result.Charging;
                reading.CheckedAt = DateTime.Now;
                if (result.Percent.HasValue) reading.Detail = "设备电量已读取";
                return;
            }
        }

        internal static MouseDpiState ReadDpi(IEnumerable<HidInfo> devices, string key) {
            if (String.IsNullOrEmpty(key)) return null;
            foreach (var group in devices.Where(h => h.Transport == "usb" && Find(h.Vid, h.Pid) != null)
                    .GroupBy(h => h.PhysicalId, StringComparer.OrdinalIgnoreCase)) {
                var input = group.First();
                var model = Find(input.Vid, input.Pid);
                bool correctDevice = model.Protocol == UsbMouseProtocol.Mchose
                    ? MchoseMouse.MatchesKey(input, key) : BatteryReader.UsbDeviceKey(input, "mouse") == key;
                if (!correctDevice) continue;
                foreach (var info in Channels(group, model)) {
                    var state = UseChannel(info, link => {
                        switch (model.Protocol) {
                            case UsbMouseProtocol.Razer: return RazerMouse.ReadDpi(link);
                            case UsbMouseProtocol.Mchose: return MchoseMouse.ReadDpi(link, model, key);
                            case UsbMouseProtocol.Dareu: return DareuMouse.ReadDpi(link);
                            case UsbMouseProtocol.Aula: return AulaMouse.ReadDpi(link);
                            default: return null;
                        }
                    });
                    if (state != null) return state;
                }
            }
            return null;
        }

        internal static bool IsReadOnlyKey(IEnumerable<HidInfo> devices, string key) {
            return devices.Any(info => {
                var model = Find(info.Vid, info.Pid);
                return info.Transport == "usb" && model != null && (model.Protocol == UsbMouseProtocol.Mchose
                    ? MchoseMouse.MatchesKey(info, key) : BatteryReader.UsbDeviceKey(info, "mouse") == key);
            });
        }

        // Separate monitor processes can access the same receiver for battery and DPI.
        static T UseChannel<T>(HidInfo info, Func<HidLink, T> read) where T : class {
            try {
                using (var gate = new Mutex(false, @"Local\PeripheralBatteryTray.Mouse." + BatteryReader.UsbDeviceKey(info, "mouse"))) {
                    bool acquired;
                    try { acquired = gate.WaitOne(850); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return null;
                    try { using (var link = new HidLink(info)) return read(link); }
                    finally { gate.ReleaseMutex(); }
                }
            } catch (Win32Exception) {} catch (TimeoutException) {} catch (UnauthorizedAccessException) {}
            return null;
        }

        internal static byte[] RequestFeature(HidLink link, byte[] request, int delay) {
            if (!HidNative.HasReport(link.Handle, 2, request[0], request.Length)) return null;
            if (!HidNative.HidD_SetFeature(link.Handle, request, request.Length)) return null;
            Thread.Sleep(delay);
            var response = new byte[request.Length];
            response[0] = request[0];
            return HidNative.HidD_GetFeature(link.Handle, response, response.Length) && response[0] == request[0]
                ? response : null;
        }

        internal static int LittleWord(byte[] buffer, int offset) {
            return buffer[offset] | (buffer[offset + 1] << 8);
        }
    }
}
