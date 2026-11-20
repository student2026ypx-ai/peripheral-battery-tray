using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBattery {
    public sealed class HidInfo {
        public string Path, Product;
        public string Transport, PhysicalId;
        public Guid Container;
        public uint Node, PhysicalNode;
        public ushort Vid, Pid, UsagePage, Usage, InputLength, OutputLength, FeatureLength;
        internal HidBattery.ValueCaps[] BatteryFields;
        public override string ToString() {
            return String.Format("{0:X4}:{1:X4} page={2:X4} usage={3:X4} in={4} out={5} feature={6} product={7}",
                Vid, Pid, UsagePage, Usage, InputLength, OutputLength, FeatureLength, Product);
        }
    }

    public static class HidNative {
        [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DeviceData { public int Size; public Guid Class; public uint Node; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort VendorID, ProductID, Version; }
        [StructLayout(LayoutKind.Sequential)] struct Caps {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, uint f);
        [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid g, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr dev);
        [DllImport("setupapi.dll", EntryPoint="SetupDiGetDeviceInterfaceDetailW", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        static extern bool SetupDiGetDeviceInterfaceDetailWithNode(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceData dev);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern SafeFileHandle CreateFile(string p, uint a, uint s, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError=true)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes a);
        [DllImport("hid.dll", SetLastError=true)] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError=true)]
        [return:MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")]
        [return:MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll")]
        static extern int HidP_InitializeReportForID(int type, byte id, IntPtr data, [Out] byte[] report, uint length);
        [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError=true)]
        [return:MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError=true)]
        [return:MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError=true)]
        [return:MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, int length);

        internal static bool HasReport(SafeFileHandle handle, int type, byte id, int length) {
            IntPtr data;
            if (!HidD_GetPreparsedData(handle, out data)) return false;
            try {
                return HidP_InitializeReportForID(type, id, data, new byte[length], (uint)length) == 0x00110000;
            } finally { HidD_FreePreparsedData(data); }
        }

        public static List<HidInfo> Enumerate() {
            var found = new List<HidInfo>(); Guid guid; HidD_GetHidGuid(out guid);
            IntPtr set=SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set==new IntPtr(-1)) throw new Win32Exception();
            try {
                for(uint i=0;;i++) {
                    var data=new InterfaceData(); data.Size=Marshal.SizeOf(data);
                    if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref data)) break;
                    uint required; SetupDiGetDeviceInterfaceDetail(set,ref data,IntPtr.Zero,0,out required,IntPtr.Zero);
                    if(required==0) continue;
                    IntPtr detail=Marshal.AllocHGlobal((int)required);
                    try {
                        Marshal.WriteInt32(detail,IntPtr.Size==8?8:6);
                        var device = new DeviceData {Size=Marshal.SizeOf(typeof(DeviceData))};
                        if(!SetupDiGetDeviceInterfaceDetailWithNode(set,ref data,detail,required,out required,ref device)) continue;
                        string path=Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                        using(var h=CreateFile(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
                            if(h.IsInvalid) continue;
                            var attr=new Attributes(); attr.Size=Marshal.SizeOf(attr);
                            if(!HidD_GetAttributes(h,ref attr)) continue;
                            IntPtr pp; if(!HidD_GetPreparsedData(h,out pp)) continue;
                            Caps caps;
                            HidBattery.ValueCaps[] batteryFields;
                            try {
                                if(HidP_GetCaps(pp,out caps)<0) continue;
                                batteryFields = caps.FeatureReportByteLength > 1 ? HidBattery.FindFields(pp,caps.NumberFeatureValueCaps) : null;
                            }
                            finally { HidD_FreePreparsedData(pp); }
                            bool input = caps.UsagePage==1 && (caps.Usage==2 || caps.Usage==6 || caps.Usage==7);
                            bool aimPoint = AsusAimPointMouse.IsProduct(attr.VendorID, attr.ProductID);
                            bool supportedMouse = UsbMouseSupport.Find(attr.VendorID, attr.ProductID) != null;
                            if(!input && attr.VendorID!=0x046D && attr.VendorID!=0x3837 && !aimPoint && !supportedMouse &&
                               !UsbKeyboardSupport.KnownId(attr.VendorID, attr.ProductID) &&
                               ExpandedUsbBattery.Find(attr.VendorID, attr.ProductID) == null &&
                               UsbHeadsetSupport.Find(attr.VendorID, attr.ProductID) == null && batteryFields == null) continue;
                            var info = new HidInfo {Path=path,Node=device.Node,Vid=attr.VendorID,Pid=attr.ProductID,
                                UsagePage=caps.UsagePage,Usage=caps.Usage,InputLength=caps.InputReportByteLength,
                                OutputLength=caps.OutputReportByteLength,FeatureLength=caps.FeatureReportByteLength,BatteryFields=batteryFields};
                            if(!FindTransport(info)) continue;
                            byte[] name=new byte[512];
                            if(HidD_GetProductString(h,name,name.Length))
                                info.Product=Encoding.Unicode.GetString(name).TrimEnd('\0').Trim();
                            if(string.IsNullOrWhiteSpace(info.Product))
                                info.Product=WindowsDevices.BusName(info.PhysicalNode)??WindowsDevices.FriendlyName(info.PhysicalNode);
                            found.Add(info);
                        }
                    } finally { Marshal.FreeHGlobal(detail); }
                }
            } finally { SetupDiDestroyDeviceInfoList(set); }
            return found;
        }

        static bool FindTransport(HidInfo info) {
            uint node=info.Node;
            for(int depth=0;depth<12;depth++) {
                string id=WindowsDevices.InstanceId(node);
                if(id.StartsWith(@"BTHENUM\",StringComparison.OrdinalIgnoreCase) ||
                   id.StartsWith(@"BTHLE\",StringComparison.OrdinalIgnoreCase) ||
                   id.StartsWith(@"BTHLEDEVICE\",StringComparison.OrdinalIgnoreCase)) {
                    info.Transport="bluetooth";
                } else if(id.StartsWith(@"USB\VID_",StringComparison.OrdinalIgnoreCase) &&
                          id.Split('\\')[1].IndexOf("&MI_",StringComparison.OrdinalIgnoreCase)<0) {
                    info.Transport="usb";
                }
                if(info.Transport!=null) {
                    info.PhysicalNode=node;
                    info.PhysicalId=id;
                    info.Container=WindowsDevices.Container(node);
                    if(info.Container==Guid.Empty) info.Container=WindowsDevices.Container(info.Node);
                    return info.Container!=WindowsDevices.SystemContainer;
                }
                uint parent;
                if(!WindowsDevices.TryParent(node,out parent)) break;
                node=parent;
            }
            return false;
        }
    }

    public sealed class HidLink : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct Overlapped { public IntPtr Internal,InternalHigh; public uint Offset,OffsetHigh; public IntPtr Event; }
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadFile(SafeFileHandle h,IntPtr buffer,uint length,IntPtr count,IntPtr ov);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteFile(SafeFileHandle h,IntPtr buffer,uint length,IntPtr count,IntPtr ov);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetOverlappedResult(SafeFileHandle h,IntPtr ov,out uint count,bool wait);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool CancelIoEx(SafeFileHandle h,IntPtr ov);
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateEvent(IntPtr sec,bool manual,bool initial,string name);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h,uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        public readonly HidInfo Info;
        public readonly SafeFileHandle Handle;
        public HidLink(HidInfo info, bool readOnly = false) {
            Info=info; Handle=HidNative.CreateFile(info.Path,readOnly ? 0x80000000U : 0xC0000000U,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(Handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot open battery channel");
        }
        byte[] Transfer(byte[] payload,bool reading,int timeout) {
            int size=reading?Info.InputLength:Info.OutputLength;
            if(size==0) throw new InvalidOperationException("No matching HID report channel");
            if(!reading && payload.Length>size) throw new InvalidOperationException("Report too long");
            IntPtr buffer=Marshal.AllocHGlobal(size),ov=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Overlapped)));
            IntPtr evt=CreateEvent(IntPtr.Zero,true,false,null);
            try {
                byte[] bytes=new byte[size]; if(!reading) Array.Copy(payload,bytes,payload.Length);
                Marshal.Copy(bytes,0,buffer,size);
                Marshal.StructureToPtr(new Overlapped {Event=evt},ov,false);
                bool result=reading?ReadFile(Handle,buffer,(uint)size,IntPtr.Zero,ov):WriteFile(Handle,buffer,(uint)size,IntPtr.Zero,ov);
                if(!result && Marshal.GetLastWin32Error()!=997) throw new Win32Exception(Marshal.GetLastWin32Error());
                if(!result && WaitForSingleObject(evt,(uint)timeout)!=0) {
                    CancelIoEx(Handle,ov); uint ignored; GetOverlappedResult(Handle,ov,out ignored,true);
                    return null;
                }
                uint count; if(!GetOverlappedResult(Handle,ov,out count,false)) throw new Win32Exception();
                byte[] response=new byte[count]; Marshal.Copy(buffer,response,0,(int)count); return response;
            } finally { CloseHandle(evt); Marshal.FreeHGlobal(ov); Marshal.FreeHGlobal(buffer); }
        }
        public void Write(byte[] payload) { if(Transfer(payload,false,1000)==null) throw new TimeoutException("HID query timed out"); }
        public byte[] Read(int timeout) {return Transfer(null,true,timeout);}
        internal byte[] ReadReply(Func<byte[], bool> matches, int timeout) {
            var time = Stopwatch.StartNew();
            while (time.ElapsedMilliseconds < timeout) {
                var reply = Read(Math.Max(1, timeout - (int)time.ElapsedMilliseconds));
                if (reply == null || matches(reply)) return reply;
            }
            return null;
        }

        internal bool WriteFeature(byte[] request) {
            return request.Length == Info.FeatureLength && HidNative.HasReport(Handle, 2, request[0], request.Length) &&
                HidNative.HidD_SetFeature(Handle, request, request.Length);
        }

        internal byte[] ReadFeature(byte reportId) {
            var reply = new byte[Info.FeatureLength];
            reply[0] = reportId;
            return HidNative.HidD_GetFeature(Handle, reply, reply.Length) && reply[0] == reportId ? reply : null;
        }

        internal byte[] ReadInputReport(byte reportId) {
            var reply = new byte[Info.InputLength];
            reply[0] = reportId;
            return HidNative.HidD_GetInputReport(Handle, reply, reply.Length) && reply[0] == reportId ? reply : null;
        }

        internal static T Use<T>(HidInfo info, string kind, Func<HidLink, T> read, bool readOnly = false) where T : class {
            try {
                using (var gate = new Mutex(false, @"Local\PeripheralBatteryTray.Hid." + BatteryReader.UsbDeviceKey(info, kind))) {
                    bool acquired;
                    try { acquired = gate.WaitOne(800); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return null;
                    try { using (var link = new HidLink(info, readOnly)) return read(link); }
                    finally { gate.ReleaseMutex(); }
                }
            } catch (Win32Exception) {} catch (TimeoutException) {} catch (UnauthorizedAccessException) {}
            return null;
        }
        public void Dispose() { Handle.Dispose(); }
    }
}
