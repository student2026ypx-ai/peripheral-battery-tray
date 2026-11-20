using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery {
    internal static class LogitechHeadsetBattery {
        const byte Root = 0, Get = 1, BridgeSend = 0x11, BridgeReply = 0x10;
        static readonly byte[] ProX2Prefix = { 0x51 };
        static readonly byte[] G522Prefix = { 0x50,0x23 };

        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return Centurion(0x0AF7,"Logitech G PRO X 2 LIGHTSPEED",ProX2Prefix);
            yield return Centurion(0x0B18,"Logitech G522 LIGHTSPEED",G522Prefix);
            yield return Centurion(0x0B45,"Logitech G PRO X 3 LIGHTSPEED",G522Prefix);
            yield return new UsbBatteryDevice(0x046D,0x0B1C,"headset","Logitech ASTRO A50 Gen 5",true,
                info => ExpandedUsbBattery.Page(info,0xFF32) && info.Usage == 0x0074 &&
                    info.InputLength == 64 && info.OutputLength == 64,
                link => ExpandedUsbBattery.Ask(link,new byte[]{2,0x0C,3,0,6,0x0C},ParseAstro,1000,2));
        }

        static UsbBatteryDevice Centurion(ushort product, string name, byte[] prefix) {
            return new UsbBatteryDevice(0x046D,product,"headset",name,true,
                info => ExpandedUsbBattery.Page(info,0xFFA0) && info.Usage == 1 &&
                    ExpandedUsbBattery.OnInterface(info,3) && info.InputLength == 64 && info.OutputLength == 64,
                link => ReadCenturion(link,prefix));
        }

        internal static BatteryState ParseAstro(byte[] frame) {
            return frame != null && frame.Length >= 9 && frame[0] == 2 && frame[1] == 0x0C &&
                frame[2] >= 6 && frame[2]+3 <= frame.Length && frame[3] == 0 && frame[4] == 6 &&
                frame[6] <= 100 && frame[8] <= 1 ? ExpandedUsbBattery.Level(frame[6],frame[8] == 1) : null;
        }

        internal static byte[] CenturionFrame(byte[] prefix, params byte[] payload) {
            var report = new byte[64];
            prefix.CopyTo(report,0);
            report[prefix.Length] = (byte)(payload.Length+1);
            payload.CopyTo(report,prefix.Length+2);
            return report;
        }

        static byte[] Payload(byte[] frame, byte[] prefix) {
            int header = prefix.Length;
            if (frame == null || frame.Length < header+3 || !frame.Take(header).SequenceEqual(prefix))
                return null;
            int length = frame[header]-1;
            return length >= 2 && header+2+length <= frame.Length
                ? frame.Skip(header+2).Take(length).ToArray() : null;
        }

        internal static byte[] ParseDirect(byte[] frame, byte[] prefix, byte feature) {
            var payload = Payload(frame,prefix);
            return payload != null && payload.Length >= 3 && payload[0] == feature && payload[1] == Get
                ? payload.Skip(2).ToArray() : null;
        }

        internal static byte[] ParseBridge(byte[] frame, byte[] prefix, byte bridge, byte feature) {
            var payload = Payload(frame,prefix);
            if (payload == null || payload.Length < 8 || payload[0] != bridge || payload[1] != BridgeReply ||
                payload[4] != 0 || payload[5] != feature || payload[6] != Get) return null;
            return payload.Skip(7).ToArray();
        }

        static byte[] QueryBridge(HidLink link, byte[] prefix, byte bridge, byte feature, params byte[] query) {
            var command = new byte[7+query.Length];
            command[0]=bridge;
            command[1]=BridgeSend;
            command[3]=(byte)(3+query.Length);
            command[5]=feature;
            command[6]=Get;
            query.CopyTo(command,7);
            link.Write(CenturionFrame(prefix,command));
            byte[] data = null;
            link.ReadReply(frame => (data=ParseBridge(frame,prefix,bridge,feature)) != null,950);
            return data;
        }

        internal static BatteryState ParseCenturionBattery(byte[] data) {
            if (data == null || data.Length == 0 || data[0] > 100) return null;
            bool? charging = data.Length >= 3 && data[2] <= 3
                ? (bool?)(data[2] == 1 || data[2] == 2) : null;
            return ExpandedUsbBattery.Level(data[0],charging);
        }

        internal static BatteryState ParseLegacyProX2(byte[] frame) {
            return frame != null && frame.Length >= 13 && frame[0] == 0x51 && frame[1] == 0x0B &&
                frame[8] == 4 && frame[10] <= 100
                ? ExpandedUsbBattery.Level(frame[10],frame[12] == 2) : null;
        }

        static BatteryState ReadCenturion(HidLink link, byte[] prefix) {
            if (!HidNative.HasReport(link.Handle,1,prefix[0],64) || !HidNative.HasReport(link.Handle,0,prefix[0],64))
                return null;
            link.Write(CenturionFrame(prefix,Root,Get,0,3));
            byte[] bridge = null;
            link.ReadReply(frame => (bridge=ParseDirect(frame,prefix,Root)) != null,800);
            if (bridge != null && bridge[0] != 0) {
                var feature = QueryBridge(link,prefix,bridge[0],Root,1,4);
                if (feature != null && feature[0] != 0)
                    return ParseCenturionBattery(QueryBridge(link,prefix,bridge[0],feature[0]));
            }
            return prefix[0] == 0x51 ? ExpandedUsbBattery.Ask(link,
                new byte[]{0x51,8,0,3,0x1A,0,3,0,4,0x0A},ParseLegacyProX2,950,0x51) : null;
        }
    }
}
