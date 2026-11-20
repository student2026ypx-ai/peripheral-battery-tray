using System;
using System.Collections.Generic;

namespace PeripheralBattery {
    internal static class CorsairHeadsetBattery {
        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return Routed(0x0A69,"Corsair HS80 RGB Wireless",false,false);
            yield return Routed(0x0A6B,"Corsair HS80 RGB Wireless",true,false);
            yield return Routed(0x0A3D,"Corsair Virtuoso RGB Wireless SE",false,true);
            yield return Routed(0x0A3E,"Corsair Virtuoso RGB Wireless SE",true,true);
            yield return Routed(0x0A62,"Corsair Virtuoso RGB Wireless XT",false,true);
            yield return Routed(0x0A64,"Corsair Virtuoso RGB Wireless XT",true,true);
            yield return Receiver(0x2A08,"Corsair Void Wireless V2");
            yield return Receiver(0x2A02,"Corsair Virtuoso Max Wireless");
            yield return Receiver(0x0A97,"Corsair HS80 Max Wireless");
        }

        static UsbBatteryDevice Routed(ushort product, string name, bool wireless, bool readCharge) {
            return new UsbBatteryDevice(0x1B1C,product,"headset",name,wireless,
                info => ExpandedUsbBattery.Page(info,0xFF42) && ExpandedUsbBattery.OnInterface(info,3) &&
                    info.Usage == 1 && info.InputLength >= 6 && info.OutputLength == 64,
                link => ReadRouted(link,wireless,readCharge));
        }

        internal static int? ParseProperty(byte[] frame, byte target, bool numbered, byte property) {
            if (frame == null || frame.Length < 6) return null;
            int offset = !numbered && frame[0] == 0 ? 1 : 0;
            if (frame.Length < offset+6 || frame[offset] != 1 || frame[offset+1] != target-8 ||
                frame[offset+2] != 2 || frame[offset+3] != 0) return null;
            int raw = frame[offset+4] | (frame[offset+5]<<8);
            if (property == 0x10) return raw == 1 || raw == 2 ? (int?)raw : null;
            if (property == 0x12) return raw;
            return raw <= 1000 ? (int?)raw : null;
        }

        static int? Query(HidLink link, byte target, byte property, bool numbered) {
            link.Write(numbered ? new byte[]{2,target,2,property} : new byte[]{0,2,target,2,property});
            int? value = null;
            link.ReadReply(frame => (value = ParseProperty(frame,target,numbered,property)).HasValue,850);
            return value;
        }

        static BatteryState ReadRouted(HidLink link, bool wireless, bool readCharge) {
            if (!HidNative.HasReport(link.Handle,1,2,link.Info.OutputLength) ||
                !HidNative.HasReport(link.Handle,0,1,link.Info.InputLength)) return null;
            byte target = (byte)(wireless ? 9 : 8);
            int? level = Query(link,target,0x0F,true);
            if (!level.HasValue && readCharge) {
                target = (byte)(target == 9 ? 8 : 9);
                level = Query(link,target,0x0F,true);
            }
            if (!level.HasValue || Query(link,target,0x0F,true) != level) return null;
            int? charge = readCharge ? Query(link,target,0x10,true) : null;
            return ExpandedUsbBattery.Level(level.Value/10,charge.HasValue ? (bool?)(charge.Value == 1) : null);
        }

        static UsbBatteryDevice Receiver(ushort product, string name) {
            return new UsbBatteryDevice(0x1B1C,product,"headset",name,true,
                info => ExpandedUsbBattery.OnInterface(info,4) && info.Usage == 0 && info.UsagePage == 0 &&
                    info.OutputLength == 65 && (info.InputLength == 64 || info.InputLength == 65),ReadReceiver);
        }

        static BatteryState ReadReceiver(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0,65)) return null;
            // Firmware and heartbeat requests wake the link without taking over audio control.
            link.Write(new byte[]{0,2,8,2,0x13});
            link.Write(new byte[]{0,2,8,2,0x12});
            for (int i=0;i<16 && link.Read(5) != null;i++) {}
            if (!Query(link,9,0x12,false).HasValue) return null;
            int? level = Query(link,9,0x0F,false);
            return level.HasValue && level.Value > 0 && Query(link,9,0x0F,false) == level
                ? ExpandedUsbBattery.Level(level.Value/10) : null;
        }
    }
}
