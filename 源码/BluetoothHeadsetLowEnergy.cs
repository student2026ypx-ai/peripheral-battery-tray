using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Windows.Foundation;

namespace PeripheralBattery {
    internal static class BluetoothHeadsetLowEnergy {
        static readonly Guid QcyService = new Guid("0000a001-0000-1000-8000-00805f9b34fb");
        static readonly Guid QcyWrite = new Guid("00001001-0000-1000-8000-00805f9b34fb");
        static readonly Guid QcyNotify = new Guid("00001002-0000-1000-8000-00805f9b34fb");
        static readonly Guid VivoService = new Guid("00001100-d102-11e1-9b23-00025b00a5a5");
        static readonly Guid VivoWrite = new Guid("00001101-d102-11e1-9b23-00025b00a5a5");
        static readonly Guid VivoNotify = new Guid("00001102-d102-11e1-9b23-00025b00a5a5");

        internal static BatteryState ParseAppleEarbuds(byte[] frame, ushort model) {
            if (frame == null || frame.Length != 27 || frame[0] != 7 || frame[1] != 25 || frame[2] == 0 ||
                ((frame[3]<<8)|frame[4]) != model) return null;
            int low = frame[6]&15, high = frame[6]>>4;
            bool charging = (low <= 10 && (frame[7]&0x10) != 0) ||
                (high <= 10 && (frame[7]&0x20) != 0);
            return BluetoothHeadsetFrames.Earbuds(low*10,high*10,charging,true);
        }

        internal static BatteryState ReadAppleEarbuds(ulong address, List<BluetoothHeadsetBattery.DeviceNode> nodes,
                ushort model) {
            var addresses = new HashSet<ulong>();
            if (address != 0) addresses.Add(address);
            foreach (var node in nodes) {
                ulong other;
                if (UInt64.TryParse(node.Address,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out other) &&
                    other != 0) addresses.Add(other);
            }
            if (addresses.Count == 0) return null;
            var reading = new TaskCompletionSource<BatteryState>();
            try {
                var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode=BluetoothLEScanningMode.Passive };
                TypedEventHandler<BluetoothLEAdvertisementWatcher,BluetoothLEAdvertisementReceivedEventArgs> handler = (sender,evt) => {
                    if (!addresses.Contains(evt.BluetoothAddress)) return;
                    foreach (var paper in evt.Advertisement.ManufacturerData.Where(data => data.CompanyId == 0x004C)) {
                        var state = ParseAppleEarbuds(Bytes(paper.Data),model);
                        if (state != null) reading.TrySetResult(state);
                    }
                };
                watcher.Received += handler;
                try {
                    watcher.Start();
                    return reading.Task.Wait(1350) ? reading.Task.Result : null;
                } finally {
                    watcher.Stop();
                    watcher.Received -= handler;
                }
            } catch (COMException) {} catch (UnauthorizedAccessException) {} catch (InvalidOperationException) {}
            return null;
        }

        internal static BatteryState ParseQcy(byte[] frame) {
            if (frame == null || frame.Length < 7 || frame[0] != 0xFF || frame[1]+2 != frame.Length) return null;
            BatteryState state = null;
            for (int i=2;i<frame.Length;) {
                if (i+2 > frame.Length || i+2+frame[i+1] > frame.Length) return null;
                int count = frame[i+1];
                if (frame[i] == 0x2F && count == 3) {
                    byte left=frame[i+2], right=frame[i+3];
                    state=BluetoothHeadsetFrames.Earbuds(left&127,right&127,
                        ((left&127) <= 100 && (left&128) != 0) || ((right&127) <= 100 && (right&128) != 0));
                }
                i+=2+count;
            }
            return state;
        }

        internal static BatteryState ReadQcy(List<BluetoothHeadsetBattery.DeviceNode> nodes) {
            return ReadGatt(nodes,QcyService,QcyWrite,QcyNotify,new byte[]{0xFF,3,0xFE,1,0x2F},ParseQcy,null);
        }

        internal static BatteryState ReadVivo(List<BluetoothHeadsetBattery.DeviceNode> nodes) {
            return ReadGatt(nodes,VivoService,VivoWrite,VivoNotify,new byte[]{0,0x1B,2,7},
                BluetoothHeadsetFrames.ParseVivo,new byte[]{0,0x0A,3,0});
        }

        static BatteryState ReadGatt(List<BluetoothHeadsetBattery.DeviceNode> nodes, Guid serviceId, Guid writeId,
                Guid notifyId, byte[] query, Func<byte[],BatteryState> parse, byte[] handshake) {
            var match = BluetoothBatteryService.Match(nodes,BluetoothInputBattery.ReadConnectedLowEnergy(),true);
            if (match == null) return null;
            try {
                using (var timeout = new CancellationTokenSource(2400))
                using (var device = BluetoothLEDevice.FromIdAsync(match.Id).AsTask(timeout.Token).GetAwaiter().GetResult()) {
                    if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
                    var services = device.GetGattServicesForUuidAsync(serviceId,BluetoothCacheMode.Cached)
                        .AsTask(timeout.Token).GetAwaiter().GetResult();
                    try {
                        if (services.Status != GattCommunicationStatus.Success || services.Services.Count != 1) return null;
                        var service = services.Services[0];
                        var write = Find(service,writeId,timeout.Token);
                        var notify = Find(service,notifyId,timeout.Token);
                        return write == null || notify == null ? null : QueryGatt(write,notify,query,parse,handshake,timeout.Token);
                    } finally {
                        foreach(var service in services.Services) service.Dispose();
                    }
                }
            } catch (COMException) {} catch (UnauthorizedAccessException) {}
            catch (InvalidOperationException) {} catch (OperationCanceledException) {}
            return null;
        }

        static GattCharacteristic Find(GattDeviceService service, Guid id, CancellationToken token) {
            var result = service.GetCharacteristicsForUuidAsync(id,BluetoothCacheMode.Cached)
                .AsTask(token).GetAwaiter().GetResult();
            return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count == 1
                ? result.Characteristics[0] : null;
        }

        static BatteryState QueryGatt(GattCharacteristic write, GattCharacteristic notify, byte[] query,
                Func<byte[],BatteryState> parse, byte[] handshake, CancellationToken token) {
            var option = (write.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0
                ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
            if ((notify.CharacteristicProperties & GattCharacteristicProperties.Notify) == 0) return null;
            var result = new TaskCompletionSource<BatteryState>();
            var ready = new TaskCompletionSource<bool>();
            TypedEventHandler<GattCharacteristic,GattValueChangedEventArgs> received = (sender,args) => {
                var payload = Bytes(args.CharacteristicValue);
                if (handshake != null && payload != null && payload.Length >= 4 &&
                        payload[0] == 0 && payload[1] == 0x0A && payload[2] == 0x83 && payload[3] == 0)
                    ready.TrySetResult(true);
                var state = parse(payload);
                if (state != null) result.TrySetResult(state);
            };
            notify.ValueChanged += received;
            bool subscribed = false;
            try {
                subscribed = notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token).GetAwaiter().GetResult() ==
                    GattCommunicationStatus.Success;
                if (!subscribed) return null;
                if (handshake != null) {
                    if (!WriteGatt(write,handshake,option,token)) return null;
                    ready.Task.Wait(token);
                }
                if (!WriteGatt(write,query,option,token)) return null;
                result.Task.Wait(token);
                return result.Task.Result;
            } finally {
                notify.ValueChanged -= received;
                if (subscribed) {
                    try {
                        using (var cleanup = new CancellationTokenSource(250))
                            notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None)
                                .AsTask(cleanup.Token).GetAwaiter().GetResult();
                    } catch (COMException) {} catch (OperationCanceledException) {}
                }
            }
        }

        static bool WriteGatt(GattCharacteristic write, byte[] payload, GattWriteOption option, CancellationToken token) {
            using (var command = new DataWriter()) {
                command.WriteBytes(payload);
                return write.WriteValueAsync(command.DetachBuffer(),option).AsTask(token).GetAwaiter().GetResult() ==
                    GattCommunicationStatus.Success;
            }
        }

        static byte[] Bytes(IBuffer value) {
            if (value == null || value.Length > 1024) return null;
            using (var reader = DataReader.FromBuffer(value)) {
                var bytes = new byte[value.Length];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }
    }
}
