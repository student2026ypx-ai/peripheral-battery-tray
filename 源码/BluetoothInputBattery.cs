using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace PeripheralBattery {
    internal static class BluetoothInputBattery {
        const string AddressProperty = "System.Devices.Aep.DeviceAddress";
        const string ConnectedProperty = "System.Devices.Aep.IsConnected";
        const string ContainerProperty = "System.Devices.Aep.ContainerId";

        internal sealed class LowEnergyDevice {
            public string Id, Name, Address;
            public Guid Container;
        }

        public static IEnumerable<BatteryState> Read(IEnumerable<HidInfo> devices, string kind, bool force = false) {
            var inputs = devices.Where(info => info.Transport == "bluetooth" && BatteryReader.HasUsage(info, kind))
                .GroupBy(info => info.Container == Guid.Empty ? info.PhysicalId : info.Container.ToString("N"),
                    StringComparer.OrdinalIgnoreCase).ToList();
            if (inputs.Count == 0) yield break;

            var nodes = BluetoothHeadsetBattery.ReadPresentNodes();
            var classic = BluetoothHeadsetBattery.PairedClassicDevices().Where(device => device.Connected != 0).ToList();
            List<LowEnergyDevice> lowEnergy = null;
            foreach (var input in inputs) {
                var info = input.First();
                var related = nodes.Where(node => node.Handle == info.PhysicalNode ||
                    (info.Container != Guid.Empty && node.Container == info.Container)).ToList();
                var addresses = new HashSet<string>(related.Select(node => node.Address).Where(address => address != null));
                var paired = classic.FirstOrDefault(device => addresses.Contains(device.Address.ToString("X12", CultureInfo.InvariantCulture)));
                string name;
                if (paired.Address != 0) {
                    if (!SupportsClassicKind(paired.DeviceClass, kind)) continue;
                    name = paired.Name;
                } else {
                    if (!related.Any(node => node.IsLowEnergy)) continue;
                    if (lowEnergy == null) lowEnergy = ReadConnectedLowEnergy();
                    var connected = lowEnergy.FirstOrDefault(device =>
                        (info.Container != Guid.Empty && device.Container == info.Container) || addresses.Contains(device.Address));
                    if (connected == null) continue;
                    name = connected.Name;
                }

                // Pairing and connection alone cannot identify a pointer or keyboard. The HID
                // usage above is required, and only nodes associated with that device can supply power.
                var percent = BluetoothHeadsetBattery.ReadBatteryProperty(related);
                if (!percent.HasValue) {
                    percent = HidBattery.Read(input.Select(channel => channel.Node), input);
                }
                string key = DeviceKey(info,kind);
                bool direct = false;
                if (!percent.HasValue) {
                    percent = BluetoothBatteryService.Read(key, related, force, lowEnergy);
                    direct = percent.HasValue;
                }
                if (!percent.HasValue && info.Vid == 0x046D) {
                    var channels = devices.Where(channel => channel.Transport == "bluetooth" &&
                        LogitechInputBattery.IsChannel(channel) && DeviceKey(channel,kind) == key).ToList();
                    var logitech = LogitechInputBattery.Read(channels,kind,null).FirstOrDefault(
                        reading => reading.Percent.HasValue || reading.Charging == true);
                    if (logitech != null) {
                        logitech.Connection = "bluetooth";
                        logitech.DeviceKey = key;
                        yield return logitech;
                        continue;
                    }
                }
                if (String.IsNullOrWhiteSpace(name)) name = info.Product;
                if (String.IsNullOrWhiteSpace(name)) name = kind == "mouse" ? "蓝牙鼠标" : "蓝牙键盘";
                yield return new BatteryState {
                    Kind = kind, Name = name.Trim(), Percent = percent, Connection = "bluetooth",
                    DeviceKey = key,
                    Detail = direct ? "蓝牙标准电量" : percent.HasValue ? "Windows 蓝牙电量" : "Windows 暂未收到设备电量"
                };
            }
        }

        internal static string DeviceKey(HidInfo channel, string kind) {
            var identity = channel.Container == Guid.Empty ? channel.PhysicalId : channel.Container.ToString("N");
            return WindowsDevices.PrivateKey("bluetooth|"+kind+"|"+identity);
        }

        static bool SupportsClassicKind(uint deviceClass, string kind) {
            uint major = (deviceClass >> 8) & 0x1F;
            if (major == 2 || major == 4) return false; // phone or audio/video
            if (major != 5 || (deviceClass & 0xC0) == 0) return true;
            return (deviceClass & (kind == "mouse" ? 0x80U : 0x40U)) != 0;
        }

        internal static List<LowEnergyDevice> ReadConnectedLowEnergy() {
            var results = new List<LowEnergyDevice>();
            try {
                string selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
                var keys = new[] {AddressProperty, ConnectedProperty, ContainerProperty};
                using (var timeout = new CancellationTokenSource(2500)) {
                    var devices = DeviceInformation.FindAllAsync(selector, keys, DeviceInformationKind.AssociationEndpoint)
                        .AsTask(timeout.Token).GetAwaiter().GetResult();
                    foreach (var device in devices) {
                        object connected;
                        if (!device.Pairing.IsPaired || !device.Properties.TryGetValue(ConnectedProperty, out connected) || !Equals(connected, true))
                            continue;
                        object address, container;
                        device.Properties.TryGetValue(AddressProperty, out address);
                        device.Properties.TryGetValue(ContainerProperty, out container);
                        var text = address as string;
                        ulong packed;
                        string normalized = text != null &&
                            UInt64.TryParse(text.Replace(":", "").Replace("-", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out packed)
                            ? packed.ToString("X12", CultureInfo.InvariantCulture) : null;
                        results.Add(new LowEnergyDevice {
                            Id = device.Id,
                            Name = device.Name, Address = normalized,
                            Container = container is Guid ? (Guid)container : Guid.Empty
                        });
                    }
                }
            } catch (OperationCanceledException) {
                BatteryReader.Log("Windows Bluetooth LE connection query timed out");
            } catch (COMException) {
                BatteryReader.Log("Windows Bluetooth LE connection query unavailable");
            } catch (UnauthorizedAccessException) {
                BatteryReader.Log("Windows Bluetooth LE connection query unavailable");
            }
            return results;
        }
    }
}
