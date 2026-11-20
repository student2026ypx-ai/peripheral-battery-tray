using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PeripheralBattery {
    internal static class WindowsDevices {
        const uint PresentByEnumerator = 0x101;
        const uint BufferTooSmall = 0x1A;
        static readonly DevicePropertyKey BatteryKey = new DevicePropertyKey("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static readonly DevicePropertyKey BatteryLifeKey = new DevicePropertyKey("49CD1F76-5626-4B17-A4E8-18B4AA1A2213", 10);
        static readonly DevicePropertyKey FriendlyNameKey = new DevicePropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static readonly DevicePropertyKey BusNameKey = new DevicePropertyKey("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 4);
        public static readonly Guid SystemContainer = new Guid("00000000-0000-0000-ffff-ffffffffffff");

        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string filter, uint flags);
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling=true)]
        static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling=true)]
        static extern uint CM_Get_DevNode_PropertyW(uint node, ref DevicePropertyKey property, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

        public static string[] PresentIds(string enumerator) {
            for (int attempt=0; attempt<2; attempt++) {
                uint size;
                if (CM_Get_Device_ID_List_SizeW(out size, enumerator, PresentByEnumerator)!=0 || size<2 || size>65536)
                    break;
                var buffer = new char[size];
                uint result = CM_Get_Device_ID_ListW(enumerator, buffer, size, PresentByEnumerator);
                if (result==0) return new string(buffer).Split(new[]{'\0'}, StringSplitOptions.RemoveEmptyEntries);
                if (result!=BufferTooSmall) break;
            }
            return new string[0];
        }

        public static bool TryLocate(string id, out uint node) {
            return CM_Locate_DevNodeW(out node, id, 0)==0;
        }

        public static bool TryParent(uint node, out uint parent) {
            return CM_Get_Parent(out parent, node, 0)==0;
        }

        public static string InstanceId(uint node) {
            var result = new StringBuilder(512);
            return CM_Get_Device_IDW(node, result, result.Capacity, 0)==0 ? result.ToString() : "";
        }

        public static Guid Container(uint node) {
            var bytes = new byte[16];
            var key = WindowsAudioOutputs.ContainerKey;
            uint size = 16, type;
            return CM_Get_DevNode_PropertyW(node, ref key, out type, bytes, ref size, 0)==0 &&
                type==0x0D && size==16 ? new Guid(bytes) : Guid.Empty;
        }

        internal static Guid ContainerForInstance(string instanceId) {
            uint node;
            if (!TryLocate(instanceId, out node)) return Guid.Empty;
            Guid container = Container(node);
            return container == SystemContainer ? Guid.Empty : container;
        }

        public static int? BatteryPercent(uint node) {
            return node == 0 ? (int?)null : ReadBatteryByte(node, BatteryKey) ?? ReadBatteryByte(node, BatteryLifeKey);
        }

        internal static int? ReadBatteryPercent(IEnumerable<uint> nodes) {
            foreach (uint node in nodes) {
                var value = BatteryPercent(node);
                if (value.HasValue) return value;
            }
            return null;
        }

        static int? ReadBatteryByte(uint node, DevicePropertyKey key) {
            var value = new byte[1];
            uint size=1, type;
            if (CM_Get_DevNode_PropertyW(node, ref key, out type, value, ref size, 0)!=0 ||
                type!=3 || size!=1 || value[0]>100) return null;
            return value[0];
        }

        public static string FriendlyName(uint node) {return ReadName(node, FriendlyNameKey);}
        public static string BusName(uint node) {return ReadName(node, BusNameKey);}

        static string ReadName(uint node, DevicePropertyKey key) {
            var bytes = new byte[1024];
            uint size=(uint)bytes.Length, type;
            if (CM_Get_DevNode_PropertyW(node, ref key, out type, bytes, ref size, 0)!=0 ||
                type!=0x12 || size>bytes.Length || size%2!=0) return null;
            return Encoding.Unicode.GetString(bytes, 0, (int)size).TrimEnd('\0').Trim();
        }

        public static string PrivateKey(string identity) {
            using (var sha=SHA256.Create()) {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(identity.ToUpperInvariant()));
                return BitConverter.ToString(digest).Replace("-", "");
            }
        }
    }
}
