using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace PeripheralBattery {
    internal static class BluetoothBatteryService {
        static readonly Guid BatteryService = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        static readonly Guid BatteryLevel = new Guid("00002a19-0000-1000-8000-00805f9b34fb");
        static readonly Dictionary<string, Sample> Samples = new Dictionary<string, Sample>();

        sealed class Sample {
            public int? Percent;
            public DateTime Expires;
        }

        internal static BluetoothInputBattery.LowEnergyDevice Match(
                IEnumerable<BluetoothHeadsetBattery.DeviceNode> nodes,
                IEnumerable<BluetoothInputBattery.LowEnergyDevice> candidates, bool includeClassic = false) {
            var lowEnergy = nodes.Where(node => node.IsLowEnergy || includeClassic).ToList();
            var containers = new HashSet<Guid>(lowEnergy.Select(node => node.Container).Where(id => id != Guid.Empty));
            var addresses = new HashSet<string>(lowEnergy.Select(node => node.Address).Where(value => !String.IsNullOrEmpty(value)),
                StringComparer.OrdinalIgnoreCase);
            return candidates.FirstOrDefault(device => containers.Contains(device.Container) || addresses.Contains(device.Address));
        }

        internal static int? Read(string key, List<BluetoothHeadsetBattery.DeviceNode> nodes, bool force,
                IEnumerable<BluetoothInputBattery.LowEnergyDevice> existing = null, bool includeClassic = false) {
            if ((!includeClassic && !nodes.Any(node => node.IsLowEnergy)) || String.IsNullOrEmpty(key)) return null;
            var now = DateTime.UtcNow;
            Sample cached;
            lock (Samples) {
                if (!force && Samples.TryGetValue(key, out cached) && cached.Expires > now) return cached.Percent;
            }
            var connected = Match(nodes, existing ?? BluetoothInputBattery.ReadConnectedLowEnergy(),includeClassic);
            int? percent = connected == null ? (int?)null : ReadConnectedDevice(connected.Id);
            lock (Samples) {
                foreach (var expired in Samples.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
                    Samples.Remove(expired);
                Samples[key] = new Sample { Percent = percent, Expires = now.AddSeconds(percent.HasValue ? 120 : 25) };
            }
            return percent;
        }

        internal static int? ParseLevel(byte[] value) {
            return value != null && value.Length == 1 && value[0] <= 100 ? (int?)value[0] : null;
        }

        static int? ReadConnectedDevice(string id) {
            if (String.IsNullOrEmpty(id)) return null;
            try {
                using (var timeout = new CancellationTokenSource(1800))
                using (var device = BluetoothLEDevice.FromIdAsync(id).AsTask(timeout.Token).GetAwaiter().GetResult()) {
                    if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
                    var services = device.GetGattServicesForUuidAsync(BatteryService, BluetoothCacheMode.Cached)
                        .AsTask(timeout.Token).GetAwaiter().GetResult();
                    if (services.Status != GattCommunicationStatus.Success) return null;
                    try {
                        foreach (var service in services.Services) {
                            var levels = service.GetCharacteristicsForUuidAsync(BatteryLevel, BluetoothCacheMode.Cached)
                                .AsTask(timeout.Token).GetAwaiter().GetResult();
                            if (levels.Status != GattCommunicationStatus.Success) continue;
                            foreach (var level in levels.Characteristics) {
                                if ((level.CharacteristicProperties & GattCharacteristicProperties.Read) == 0) continue;
                                var read = level.ReadValueAsync(BluetoothCacheMode.Uncached)
                                    .AsTask(timeout.Token).GetAwaiter().GetResult();
                                if (read.Status != GattCommunicationStatus.Success || read.Value == null || read.Value.Length != 1) continue;
                                using (var reader = DataReader.FromBuffer(read.Value)) {
                                    var percent = ParseLevel(new[]{reader.ReadByte()});
                                    if (percent.HasValue) return percent;
                                }
                            }
                        }
                    } finally {
                        foreach (var service in services.Services) service.Dispose();
                    }
                }
            } catch (OperationCanceledException) {} catch (COMException) {} catch (UnauthorizedAccessException) {}
            return null;
        }
    }
}
