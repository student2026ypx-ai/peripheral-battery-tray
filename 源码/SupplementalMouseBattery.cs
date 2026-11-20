using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    internal static class SupplementalMouseBattery {
        internal static IEnumerable<UsbBatteryDevice> Devices() {
            foreach (ushort product in new ushort[]{0x0D97,0x0F98}) {
                yield return MouseReceiverBattery.UniqueDevice(0x03F0,product,"HyperX Pulsefire Haste 2 Wireless",
                    HyperXChannel,ReadHyperX,readDpi:ReadHyperXDpi);
            }
            yield return MouseReceiverBattery.UniqueDevice(0x320F,0x706E,"HECATE G3M Pro",
                G3mChannel,ReadG3m,readDpi:ReadG3mDpi);
        }

        static bool HyperXChannel(HidInfo info) {
            return info.UsagePage == 0xFF00 && info.Usage == 1 && ExpandedUsbBattery.OnInterface(info,2) &&
                info.InputLength == 64 && info.OutputLength == 64;
        }

        static byte[] QueryHyperX(HidLink link, byte command, byte subcommand, int minimumReply) {
            byte replyId = (byte)(command+1);
            if (!HidNative.HasReport(link.Handle,1,command,64) || !HidNative.HasReport(link.Handle,0,replyId,64))
                return null;
            var request = new byte[64];
            request[0] = command;
            request[1] = subcommand;
            link.Write(request);
            Thread.Sleep(12);
            return link.ReadReply(frame => frame.Length >= minimumReply && frame[0] == replyId &&
                frame[1] == subcommand,550);
        }

        static bool HyperXOnline(HidLink link) {
            var reply = QueryHyperX(link,0x52,1,3);
            return reply != null && reply[2] == 1;
        }

        internal static BatteryState ParseHyperX(byte[] frame) {
            if (frame == null || frame.Length < 10 || frame[0] != 0x51 || frame[1] != 2 || frame[2] > 100)
                return null;
            bool? charging = frame[3] <= 2 ? (bool?)(frame[3] != 0) : null;
            return ExpandedUsbBattery.Level(frame[2],charging);
        }

        static BatteryState ReadHyperX(HidLink link) {
            return HyperXOnline(link) ? ParseHyperX(QueryHyperX(link,0x50,2,10)) : null;
        }

        internal static MouseDpiState ParseHyperXDpi(byte[] frame) {
            if (frame == null || frame.Length < 29 || frame[0] != 0x33 || frame[1] != 2 ||
                frame[4] > 4 || (frame[3]&0xE0) != 0 || (frame[3]&(1<<frame[4])) == 0) return null;
            var stages = new List<int>();
            int active = -1;
            for (int index=0;index<5;index++) {
                if ((frame[3]&(1<<index)) == 0) continue;
                int dpi = (UsbMouseSupport.LittleWord(frame,5+index*5)+1)*50;
                if (dpi < 200 || dpi > 26000) return null;
                if (index == frame[4]) active = stages.Count;
                stages.Add(dpi);
            }
            return MouseDpiQueries.Stages(active,stages.ToArray(),stages.ToArray());
        }

        static MouseDpiState ReadHyperXDpi(HidLink link) {
            return HyperXOnline(link) ? ParseHyperXDpi(QueryHyperX(link,0x32,2,29)) : null;
        }

        static bool G3mChannel(HidInfo info) {
            return info.UsagePage == 0xFF1C && info.Usage == 0x92 &&
                info.InputLength == 64 && info.OutputLength == 64;
        }

        internal static BatteryState ParseG3m(byte[] frame) {
            if (frame == null || frame.Length != 64 || frame[0] != 4 || frame[3] != 0x1A ||
                frame[7] >= 0xFE || frame[8] > 100 || !frame.Skip(5).Any(value => value != 0)) return null;
            return ExpandedUsbBattery.Level(frame[8]);
        }

        static BatteryState ReadG3m(HidLink link) {
            return ExpandedUsbBattery.Ask(link,new byte[]{4,0x20,0,0x1A,6},ParseG3m,800,4);
        }

        internal static byte[] G3mQuery(byte command, int length, int offset) {
            var request = new byte[64];
            request[0] = 4;
            request[3] = command;
            request[4] = (byte)length;
            request[5] = (byte)offset;
            request[6] = (byte)(offset>>8);
            int sum = request[3]+request[4]+request[5]+request[6];
            request[1] = (byte)sum;
            request[2] = (byte)(sum>>8);
            return request;
        }

        static byte[] QueryG3m(HidLink link, byte command, int length = 0, int offset = 0) {
            link.Write(G3mQuery(command,length,offset));
            return link.ReadReply(frame => frame.Length == 64 && frame[0] == 4 && frame[3] == command &&
                frame[7] < 0xFE,650);
        }

        static byte[] ReadG3mBlock(HidLink link, byte command, int length, int offset, int chunkSize) {
            var data = new byte[length];
            for (int copied=0;copied<length;copied+=chunkSize) {
                int count = Math.Min(chunkSize,length-copied);
                var reply = QueryG3m(link,command,count,offset+copied);
                if (reply == null) return null;
                Array.Copy(reply,8,data,copied,count);
            }
            return data;
        }

        internal static MouseDpiState ParseG3mDpi(byte[] header, byte[] profile) {
            if (header == null || header.Length != 34 || profile == null || profile.Length != 84 ||
                header[11] == 0 || header[12] < 1 || header[12] > 7 || profile[12] >= header[12]) return null;
            int sensor = UsbMouseSupport.LittleWord(header,9);
            int step = header[11], minimum = UsbMouseSupport.LittleWord(header,14);
            int active = -1;
            var stages = new List<int>();
            for (int index=0;index<header[12];index++) {
                int record = 14+9*index;
                if (profile[record] == 0) continue;
                int raw = UsbMouseSupport.LittleWord(profile,record+2);
                int dpi = raw*step;
                if (sensor == 13093) dpi = minimum+(raw > 48 ? 48*step+200*(raw-48) : dpi);
                if (sensor == 13193 || sensor == 13205) dpi += minimum;
                if (dpi < 50 || dpi > 26000) return null;
                if (index == profile[12]) active = stages.Count;
                stages.Add(dpi);
            }
            return MouseDpiQueries.Stages(active,stages.ToArray(),stages.ToArray());
        }

        static MouseDpiState ReadG3mDpi(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,4,64) || !HidNative.HasReport(link.Handle,0,4,64))
                return null;
            var online = QueryG3m(link,0xAA);
            if (online == null || online[8] != 0xFF) return null;
            try {
                if (QueryG3m(link,1) == null) return null;
                var header = ReadG3mBlock(link,3,34,0,24);
                if (header == null) return null;
                int chunk = header[6] >= 1 && header[6] <= 56 ? header[6] : 24;
                var active = ReadG3mBlock(link,5,1,0,chunk);
                if (active == null) return null;
                return ParseG3mDpi(header,ReadG3mBlock(link,5,84,86*active[0],chunk));
            } finally {
                Thread.Sleep(10);
                QueryG3m(link,2);
            }
        }
    }
}
