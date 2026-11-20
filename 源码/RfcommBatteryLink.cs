using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace PeripheralBattery {
    internal sealed class RfcommBatteryLink : IDisposable {
        readonly CancellationToken token;
        readonly DataReader reader;
        readonly DataWriter writer;
        readonly List<byte> buffer = new List<byte>();

        RfcommBatteryLink(StreamSocket socket, CancellationToken token) {
            this.token = token;
            reader = new DataReader(socket.InputStream) { InputStreamOptions=InputStreamOptions.Partial };
            writer = new DataWriter(socket.OutputStream);
        }

        internal static BatteryState Use(ulong address, Guid serviceId, Func<RfcommBatteryLink,BatteryState> read,
                int timeoutMs = 3400) {
            if (address == 0) return null;
            try {
                using (var timeout = new CancellationTokenSource(timeoutMs))
                using (var device = BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(timeout.Token).GetAwaiter().GetResult()) {
                    if (device == null || !device.DeviceInformation.Pairing.IsPaired ||
                        device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
                    var id = RfcommServiceId.FromUuid(serviceId);
                    var services = device.GetRfcommServicesForIdAsync(id,BluetoothCacheMode.Cached)
                        .AsTask(timeout.Token).GetAwaiter().GetResult();
                    if (services.Services.Count == 0) {
                        services = device.GetRfcommServicesForIdAsync(id,BluetoothCacheMode.Uncached)
                            .AsTask(timeout.Token).GetAwaiter().GetResult();
                    }
                    try {
                        if (services.Error != BluetoothError.Success || services.Services.Count != 1) return null;
                        var service = services.Services[0];
                        using (var socket = new StreamSocket()) {
                            socket.ConnectAsync(service.ConnectionHostName,service.ConnectionServiceName)
                                .AsTask(timeout.Token).GetAwaiter().GetResult();
                            using (var link = new RfcommBatteryLink(socket,timeout.Token)) return read(link);
                        }
                    } finally {
                        foreach (var service in services.Services) service.Dispose();
                    }
                }
            } catch (OperationCanceledException) {} catch (COMException) {}
            catch (UnauthorizedAccessException) {} catch (InvalidOperationException) {}
            return null;
        }

        internal void Send(byte[] data) {
            writer.WriteBytes(data);
            if (writer.StoreAsync().AsTask(token).GetAwaiter().GetResult() != data.Length)
                throw new InvalidOperationException("Incomplete headset query");
        }

        internal byte[] Receive(Func<List<byte>,byte[]> takeFrame) {
            try {
                while (!token.IsCancellationRequested) {
                    byte[] packet = takeFrame(buffer);
                    if (packet != null) return packet;
                    uint received = reader.LoadAsync(512).AsTask(token).GetAwaiter().GetResult();
                    if (received == 0 || buffer.Count+received > 4096) return null;
                    var chunk = new byte[received];
                    reader.ReadBytes(chunk);
                    buffer.AddRange(chunk);
                }
            } catch (OperationCanceledException) {} catch (COMException) {}
            return null;
        }

        public void Dispose() {
            writer.Dispose();
            reader.Dispose();
        }
    }
}
