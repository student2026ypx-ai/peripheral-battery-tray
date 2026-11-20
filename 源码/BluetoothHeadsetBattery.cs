using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PeripheralBattery {
    public static class BluetoothHeadsetBattery {
        public const string DefaultName = "耳机";
        static readonly Guid HandsFreeService = new Guid("0000111e-0000-1000-8000-00805f9b34fb");
        static readonly Guid StereoSinkService = new Guid("0000110b-0000-1000-8000-00805f9b34fb");
        static readonly Guid HeadsetService = new Guid("00001108-0000-1000-8000-00805f9b34fb");
        static readonly Guid LeBatteryService = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        static readonly Regex RootAddress = new Regex(@"^BTH(?:ENUM|LE)\\DEV_([0-9A-F]{12})\\", RegexOptions.IgnoreCase);
        static readonly Regex InstanceAddress = new Regex(@"(?<![0-9A-F])([0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase);
        static string lastSelectedKey;
        static BatteryState lastUsbReading;
        static DateTime nextUsbRead;

        internal sealed class DeviceNode {
            public uint Handle;
            public string Address;
            public Guid Container, Service;
            public bool IsRoot, IsLowEnergy;
            public bool IsPlaybackProfile {
                get { return Service == StereoSinkService || Service == HandsFreeService || Service == HeadsetService; }
            }
        }

        sealed class Headset {
            public string Key, Name, Connection;
            public bool IsDefault, IsUsb;
            public Func<BatteryState> ReadBattery;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DeviceSearch {
            public int Size;
            public int Authenticated, Remembered, Unknown, Connected, Inquiry;
            public byte TimeoutMultiplier;
            public IntPtr Radio;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct BluetoothDevice {
            public int Size;
            public ulong Address;
            public uint DeviceClass;
            public int Connected, Remembered, Authenticated;
            // Two SYSTEMTIME structures (eight WORDs each).
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public ushort[] Times;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstDevice(ref DeviceSearch search, ref BluetoothDevice device);
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern bool BluetoothFindNextDevice(IntPtr search, ref BluetoothDevice device);
        [DllImport("bthprops.cpl")]
        static extern bool BluetoothFindDeviceClose(IntPtr search);

        public static BatteryState Read(bool force = false) {
            var state = new BatteryState {Kind = "headset", Name = DefaultName, Detail = "耳机未连接"};
            var nodes = ReadPresentNodes();
            var outputs = WindowsAudioOutputs.Read(WindowsDevices.ContainerForInstance);
            var bluetooth = nodes.Count == 0 ? Enumerable.Empty<Headset>() : FindHeadsets(nodes, outputs, force);
            var headset = bluetooth.Concat(FindUsbHeadsets(outputs))
                .OrderByDescending(candidate => candidate.IsDefault)
                .ThenByDescending(candidate => candidate.Key == lastSelectedKey)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (headset == null || !headset.IsUsb) lastUsbReading = null;
            if (headset == null) return state;

            lastSelectedKey = headset.Key;
            if (!force && lastUsbReading != null && lastUsbReading.DeviceKey == headset.Key && DateTime.UtcNow < nextUsbRead)
                return lastUsbReading;
            state = headset.ReadBattery() ?? new BatteryState { Detail = "系统暂未返回耳机电量" };
            state.Kind = "headset";
            state.DeviceKey = headset.Key;
            state.Name = headset.Name;
            state.Connection = headset.Connection;
            state.CheckedAt = DateTime.Now;
            if (headset.IsUsb) {
                lastUsbReading = state;
                nextUsbRead = DateTime.UtcNow.AddMinutes(2);
            }
            return state;
        }

        static IEnumerable<Headset> FindUsbHeadsets(WindowsAudioOutputs outputs) {
            if (outputs.ActiveContainers.Count == 0) yield break;
            var usb = WindowsDevices.PresentIds("USB");
            var models = UsbHeadsetSupport.ConnectedModels(usb);
            var relatedNodes = new Dictionary<Guid, List<uint>>();
            foreach (string id in usb) {
                if (!id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)) continue;
                uint node;
                if (!WindowsDevices.TryLocate(id, out node)) continue;
                Guid container = ReadContainer(node);
                if (!outputs.HeadsetOutputs.ContainsKey(container) &&
                    !(outputs.ActiveContainers.Contains(container) && models.ContainsKey(container))) continue;
                List<uint> related;
                if (!relatedNodes.TryGetValue(container, out related)) relatedNodes.Add(container, related = new List<uint>());
                related.Add(node);
            }
            foreach (var item in relatedNodes) {
                var container = item.Key;
                var deviceNodes = item.Value;
                UsbHeadsetModel model;
                models.TryGetValue(container, out model);
                yield return new Headset {
                    Key = WindowsDevices.PrivateKey("usb|headset|" + container.ToString("N")),
                    Name = model == null ? outputs.HeadsetOutputs[container].Name : model.Name,
                    Connection = model == null || (model.Expanded != null && !model.Expanded.Wireless) ? "usb" : "wireless",
                    IsUsb = true,
                    IsDefault = container == outputs.DefaultContainer,
                    ReadBattery = () => ReadUsbBattery(container, deviceNodes, model)
                };
            }
        }

        static BatteryState ReadUsbBattery(Guid container, List<uint> deviceNodes, UsbHeadsetModel model) {
            var reported = WindowsDevices.ReadBatteryPercent(deviceNodes);
            if (model == null && reported.HasValue) return new BatteryState { Percent = reported, Detail = "设备标准电量" };
            var channels = HidNative.Enumerate().Where(info => info.Transport == "usb" && info.Container == container &&
                !BatteryReader.HasUsage(info, "mouse") && !BatteryReader.HasUsage(info, "keyboard")).ToList();
            var specific = model == null ? null : UsbHeadsetSupport.Read(model, channels);
            if (specific != null) return specific;
            var percent = reported ?? HidBattery.Read(channels.Select(info => info.Node), channels);
            return percent.HasValue ? new BatteryState { Percent = percent, Detail = "设备标准电量" } : null;
        }

        static BatteryState ReadBluetoothBattery(string key, string name, ulong address, List<DeviceNode> nodes, bool force) {
            var percent = ReadBatteryProperty(nodes);
            if (percent.HasValue) return new BatteryState { Percent = percent, Detail = "Windows 蓝牙电量" };
            percent = BluetoothBatteryService.Read(key,nodes,force,includeClassic:BluetoothHeadsetProtocols.IsListed(name));
            return percent.HasValue ? new BatteryState { Percent=percent, Detail="蓝牙标准电量" } :
                BluetoothHeadsetProtocols.Read(key,name,address,nodes,force);
        }

        static IEnumerable<Headset> FindHeadsets(List<DeviceNode> nodes, WindowsAudioOutputs outputs, bool force) {
            var claimedContainers = new HashSet<Guid>();
            foreach (var device in PairedClassicDevices()) {
                string address = device.Address.ToString("X12", CultureInfo.InvariantCulture);
                var addressedNodes = nodes.Where(node => node.Address == address).ToList();
                var containers = new HashSet<Guid>(
                    addressedNodes.Select(node => node.Container).Where(container => container != Guid.Empty));
                if (IsPhone(device.DeviceClass)) {
                    claimedContainers.UnionWith(containers);
                    continue;
                }
                var services = nodes.Where(node => node.Address == address || containers.Contains(node.Container)).ToList();
                string name = ValidName(device.Name);
                if (!IsHeadphone(device.DeviceClass) && !services.Any(node => node.IsPlaybackProfile) &&
                    !BluetoothHeadsetProtocols.IsListed(name)) continue;
                if (device.Connected == 0 && !containers.Overlaps(outputs.ActiveContainers)) continue;

                var root = addressedNodes.FirstOrDefault(node => node.IsRoot && !node.IsLowEnergy && node.Container != Guid.Empty);
                Guid containerId = root != null ? root.Container : containers.OrderBy(value => value).FirstOrDefault();
                string key = containerId != Guid.Empty ? containerId.ToString("N") : KeyForAddress(address);
                claimedContainers.UnionWith(containers);
                yield return new Headset {
                    Key = key, Name = name,
                    IsDefault = containers.Contains(outputs.DefaultContainer), Connection = "bluetooth",
                    ReadBattery = () => ReadBluetoothBattery(key,name,device.Address,services,force)
                };
            }

            // LE audio devices and active endpoints not returned by the Classic radio API.
            var activeGroups = nodes
                .Where(node => node.Container != Guid.Empty && outputs.ActiveContainers.Contains(node.Container)
                    && !claimedContainers.Contains(node.Container))
                .GroupBy(node => node.Container);
            foreach (var group in activeGroups) {
                var services = group.ToList();
                if (!services.Any(node => node.IsLowEnergy || node.IsPlaybackProfile)) continue;
                var namedNode = services.OrderByDescending(node => node.IsRoot)
                    .ThenByDescending(node => node.Service == StereoSinkService)
                    .Select(node => WindowsDevices.FriendlyName(node.Handle)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                string key = group.Key.ToString("N");
                string name = ValidName(namedNode);
                yield return new Headset {
                    Key = key, Name = name,
                    IsDefault = group.Key == outputs.DefaultContainer, Connection = "bluetooth",
                    ReadBattery = () => ReadBluetoothBattery(key,name,0,services,force)
                };
            }
        }

        static string ValidName(string name) {
            return string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        }

        static bool IsPhone(uint deviceClass) {
            return ((deviceClass >> 8) & 0x1F) == 2;
        }

        static bool IsHeadphone(uint deviceClass) {
            uint minor = (deviceClass >> 2) & 0x3F;
            return ((deviceClass >> 8) & 0x1F) == 4 && (minor == 1 || minor == 2 || minor == 6);
        }

        static string KeyForAddress(string address) {
            // Persist only a derived key when Windows does not supply a container.
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(address))).Replace("-", "");
        }

        internal static IEnumerable<BluetoothDevice> PairedClassicDevices() {
            var search = new DeviceSearch {
                Size = Marshal.SizeOf(typeof(DeviceSearch)), Connected = 1, Authenticated = 1, Remembered = 1
            };
            var device = new BluetoothDevice {Size = Marshal.SizeOf(typeof(BluetoothDevice)), Times = new ushort[16]};
            IntPtr handle = BluetoothFindFirstDevice(ref search, ref device);
            if (handle == IntPtr.Zero) yield break;
            try {
                do {
                    if (device.Address != 0) yield return device;
                } while (BluetoothFindNextDevice(handle, ref device));
            } finally {
                BluetoothFindDeviceClose(handle);
            }
        }

        internal static int? ReadBatteryProperty(IEnumerable<DeviceNode> nodes) {
            // Prefer the standard battery service or hands-free battery field over other nodes.
            foreach (var node in nodes.OrderByDescending(node => node.Service == LeBatteryService)
                         .ThenByDescending(node => node.Service == HandsFreeService)) {
                var value = WindowsDevices.BatteryPercent(node.Handle);
                if (value.HasValue) return value;
            }
            return null;
        }

        internal static List<DeviceNode> ReadPresentNodes() {
            var nodes = new List<DeviceNode>();
            foreach (string enumerator in new[] {"BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM"}) {
                foreach (string id in WindowsDevices.PresentIds(enumerator)) {
                    uint handle;
                    if (!WindowsDevices.TryLocate(id, out handle)) continue;

                    var root = RootAddress.Match(id);
                    int serviceStart = id.IndexOf('\\') + 1;
                    int serviceEnd = id.IndexOf('}', serviceStart);
                    Guid service = Guid.Empty;
                    if (serviceEnd > serviceStart && id[serviceStart] == '{')
                        Guid.TryParse(id.Substring(serviceStart, serviceEnd - serviceStart + 1), out service);

                    var address = root.Success ? root : InstanceAddress.Match(id, id.LastIndexOf('\\') + 1);
                    nodes.Add(new DeviceNode {
                        Handle = handle,
                        Address = address.Success ? address.Groups[1].Value.ToUpperInvariant() : null,
                        Container = ReadContainer(handle), Service = service, IsRoot = root.Success,
                        IsLowEnergy = enumerator == "BTHLE" || enumerator == "BTHLEDEVICE"
                    });
                }
            }
            return nodes;
        }

        static Guid ReadContainer(uint node) {
            Guid value = WindowsDevices.Container(node);
            return value == WindowsDevices.SystemContainer ? Guid.Empty : value;
        }
    }
}
