using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery {
    internal static class BluetoothHeadsetFrames {
        internal static BatteryState Earbuds(int left, int right, bool? charging = null, bool estimated = false) {
            bool hasLeft = left >= 0 && left <= 100, hasRight = right >= 0 && right <= 100;
            if (!hasLeft && !hasRight) return null;
            int level = hasLeft && hasRight ? Math.Min(left,right) : hasLeft ? left : right;
            return ExpandedUsbBattery.Level(level,charging,estimated);
        }

        internal static ushort Crc(byte[] bytes, int start, int length) {
            int crc = 0;
            for (int i=start;i<start+length;i++) {
                crc ^= bytes[i]<<8;
                for (int bit=0;bit<8;bit++) crc = ((crc<<1)^((crc&0x8000) != 0 ? 0x1021 : 0))&0xFFFF;
            }
            return (ushort)crc;
        }

        internal static byte[] TakeFrame(List<byte> bytes, byte marker, int header, Func<List<byte>,int> length) {
            while (bytes.Count != 0) {
                int start = bytes.IndexOf(marker);
                if (start < 0) {
                    bytes.Clear();
                    return null;
                }
                if (start > 0) bytes.RemoveRange(0,start);
                if (bytes.Count < header) return null;
                int size = length(bytes);
                if (size < header || size > 1028) {
                    bytes.RemoveAt(0);
                    continue;
                }
                if (bytes.Count < size) return null;
                byte[] frame = bytes.GetRange(0,size).ToArray();
                bytes.RemoveRange(0,size);
                return frame;
            }
            return null;
        }

        internal static byte[] EncodeSony(byte frameType, byte sequence, params byte[] payload) {
            var body = new List<byte> {frameType,sequence,0,0,(byte)(payload.Length>>8),(byte)payload.Length};
            body.AddRange(payload);
            body.Add(unchecked((byte)body.Sum(value => (int)value)));
            var frame = new List<byte> {0x3E};
            foreach (byte value in body) {
                if (value >= 0x3C && value <= 0x3E) {
                    frame.Add(0x3D);
                    frame.Add((byte)(value&0xEF));
                } else frame.Add(value);
            }
            frame.Add(0x3C);
            return frame.ToArray();
        }

        internal static byte[] TakeSony(List<byte> bytes) {
            while (bytes.Count != 0) {
                int start = bytes.IndexOf(0x3E);
                if (start < 0) { bytes.Clear(); return null; }
                if (start > 0) bytes.RemoveRange(0,start);
                int end = bytes.IndexOf(0x3C,1);
                int next = bytes.IndexOf(0x3E,1);
                if (next >= 0 && (end < 0 || next < end)) {
                    bytes.RemoveRange(0,next);
                    continue;
                }
                if (end < 0) return null;
                var body = new List<byte>();
                for (int i=1;i<end;i++) {
                    byte value = bytes[i];
                    if (value == 0x3D) {
                        if (++i == end || bytes[i] < 0x2C || bytes[i] > 0x2E) { body.Clear(); break; }
                        value = (byte)(bytes[i]|0x10);
                    }
                    body.Add(value);
                }
                bytes.RemoveRange(0,end+1);
                if (body.Count < 7 || body[1] > 1 || body[2] != 0 || body[3] != 0 ||
                    ((body[4]<<8)|body[5]) != body.Count-7) continue;
                if ((body.Take(body.Count-1).Sum(value => (int)value)&0xFF) == body[body.Count-1]) return body.ToArray();
            }
            return null;
        }

        static byte[] SonyExchange(RfcommBatteryLink link, ref byte sequence, byte[] query, Func<byte[],bool> accept) {
            link.Send(EncodeSony(12,sequence,query));
            byte[] frame;
            while ((frame=link.Receive(TakeSony)) != null) {
                sequence = frame[1];
                if (frame[0] != 12 && frame[0] != 14) continue;
                link.Send(EncodeSony(1,(byte)(sequence^1)));
                var payload = frame.Skip(6).Take(frame.Length-7).ToArray();
                if (accept(payload)) return payload;
            }
            return null;
        }

        internal static BatteryState ParseSonyBattery(byte[] payload, bool v2, byte type) {
            if (payload == null || payload.Length < 4 || payload[1] != type ||
                (v2 ? payload[0] != 0x23 && payload[0] != 0x25 : payload[0] != 0x11 && payload[0] != 0x13))
                return null;
            if (type == 0) return payload[2] <= 100 && payload[3] <= 3
                ? ExpandedUsbBattery.Level(payload[2],payload[3] == 2 ? (bool?)null : payload[3] == 1) : null;
            if (payload.Length < 6) return null;
            return Earbuds(payload[2] == 0 ? -1 : payload[2],payload[4] == 0 ? -1 : payload[4],
                (payload[2] > 0 && payload[2] <= 100 && payload[3] == 1) ||
                (payload[4] > 0 && payload[4] <= 100 && payload[5] == 1));
        }

        internal static BatteryState ReadSony(RfcommBatteryLink link, bool v2, byte type) {
            byte sequence = 0;
            var init = SonyExchange(link,ref sequence,new byte[]{0,0},
                payload => payload.Length >= 4 && payload[0] == 1 && payload[1] == 0);
            if (init == null) return null;
            var response = SonyExchange(link,ref sequence,new[]{(byte)(v2 ? 0x22 : 0x10),type},
                payload => ParseSonyBattery(payload,v2,type) != null);
            return ParseSonyBattery(response,v2,type);
        }

        internal static BatteryState ParseSamsung(byte[] frame) {
            if (frame == null || frame.Length < 13 || frame[0] != 0xFD || frame[frame.Length-1] != 0xDD ||
                (frame[2]&0x20) != 0 || ((frame[1]|(frame[2]<<8))&0x3FF) != frame.Length-4) return null;
            int last = frame.Length-3;
            if (Crc(frame,3,last-3) != (frame[last]|(frame[last+1]<<8))) return null;
            if (frame[3] == 0x60 && last >= 12)
                return Earbuds(frame[5],frame[6],(frame[11]&0x14) != 0);
            if (frame[3] == 0x61 && last >= 12)
                return Earbuds(frame[6],frame[7]);
            return null;
        }

        internal static BatteryState ReadSamsung(RfcommBatteryLink link) {
            byte[] frame;
            while ((frame=link.Receive(bytes => TakeFrame(bytes,0xFD,3,
                    data => 4+((data[1]|(data[2]<<8))&0x3FF)))) != null) {
                var battery = ParseSamsung(frame);
                if (battery != null) return battery;
            }
            return null;
        }

        internal static byte[] HuaweiRequest() {
            var frame = new byte[]{0x5A,0,9,0,1,8,1,0,2,0,3,0,0,0};
            ushort crc = Crc(frame,0,frame.Length-2);
            frame[frame.Length-2]=(byte)(crc>>8);
            frame[frame.Length-1]=(byte)crc;
            return frame;
        }

        internal static BatteryState ParseHuawei(byte[] frame) {
            if (frame == null || frame.Length < 8 || frame[0] != 0x5A ||
                ((frame[1]<<8)|frame[2])+5 != frame.Length || frame[3] != 0 || frame[4] != 1 ||
                (frame[5] != 8 && frame[5] != 0x27)) return null;
            int end = frame.Length-2;
            if (Crc(frame,0,end) != ((frame[end]<<8)|frame[end+1])) return null;
            int overall = -1, left = -1, right = -1;
            bool? charging = null;
            for (int i=6;i<end;) {
                if (i+2 > end || i+2+frame[i+1] > end) return null;
                int size = frame[i+1];
                if (frame[i] == 1 && size == 1 && frame[i+2] <= 100) overall=frame[i+2];
                if (frame[i] == 2 && size == 3) {
                    left=frame[i+2] == 0 ? -1 : frame[i+2];
                    right=frame[i+3] == 0 ? -1 : frame[i+3];
                }
                if (frame[i] == 3 && size == 3) charging = frame.Skip(i+2).Take(size).Contains((byte)1);
                i+=2+size;
            }
            return Earbuds(left,right,charging) ?? (overall >= 0 ? ExpandedUsbBattery.Level(overall,charging) : null);
        }

        internal static BatteryState ReadHuawei(RfcommBatteryLink link) {
            link.Send(HuaweiRequest());
            byte[] frame;
            while ((frame=link.Receive(bytes => TakeFrame(bytes,0x5A,3,
                    data => 5+(data[1]<<8)+data[2]))) != null) {
                var battery = ParseHuawei(frame);
                if (battery != null) return battery;
            }
            return null;
        }

        static byte[] TakeBose(List<byte> data) {
            if (data.Count < 4 || data.Count < data[3]+4) return null;
            var packet = data.GetRange(0,data[3]+4).ToArray();
            data.RemoveRange(0,packet.Length);
            return packet;
        }

        internal static BatteryState ParseBose(byte[] frame) {
            if (frame == null || frame.Length < 8 || frame[0] != 2 || frame[1] != 2 || (frame[2]&15) != 3 ||
                frame[3]+4 != frame.Length || frame[3]%4 != 0) return null;
            int overall = -1, left = -1, right = -1;
            for (int i=4;i<frame.Length;i+=4) {
                if (frame[i] > 100) continue;
                switch (frame[i+3]) {
                    case 0: case 4: if (overall < 0) overall=frame[i]; break;
                    case 1: left=frame[i]; break;
                    case 2: right=frame[i]; break;
                }
            }
            return Earbuds(left,right) ?? (overall >= 0 ? ExpandedUsbBattery.Level(overall) : null);
        }

        internal static BatteryState ReadBose(RfcommBatteryLink link, int product) {
            link.Send(new byte[]{0,1,1,0});
            link.Send(new byte[]{0,3,1,0});
            byte[] frame;
            while ((frame=link.Receive(TakeBose)) != null) {
                if (frame.Length < 7 || frame[0] != 0 || frame[1] != 3 || (frame[2]&15) != 3) continue;
                if (((frame[4]<<8)|frame[5]) != product) return null;
                link.Send(new byte[]{2,2,1,0});
                while ((frame=link.Receive(TakeBose)) != null) {
                    var battery = ParseBose(frame);
                    if (battery != null) return battery;
                }
                return null;
            }
            return null;
        }

        internal static int? ParseRaceEarbud(byte[] frame, out int ear) {
            ear = -1;
            if (frame == null || frame.Length != 9 || frame[0] != 5 || frame[1] != 0x5D || frame[2] != 5 ||
                frame[3] != 0 || frame[4] != 0xD6 || frame[5] != 0x0C || frame[6] != 0 ||
                frame[7] > 1 || frame[8] > 100) return null;
            ear = frame[7];
            return frame[8];
        }

        internal static BatteryState ReadRaceEarbuds(RfcommBatteryLink link) {
            link.Send(new byte[]{5,0x5A,3,0,0xD6,0x0C,0});
            link.Send(new byte[]{5,0x5A,3,0,0xD6,0x0C,1});
            int[] levels = {-1,-1};
            byte[] frame;
            while ((frame=link.Receive(bytes => TakeFrame(bytes,5,4,
                    data => 4+data[2]+(data[3]<<8)))) != null) {
                int ear;
                int? battery = ParseRaceEarbud(frame,out ear);
                if (!battery.HasValue) continue;
                levels[ear] = battery.Value;
                if (levels[0] >= 0 && levels[1] >= 0) break;
            }
            return Earbuds(levels[1],levels[0]);
        }

        internal static byte[] TakeFastPairFrame(List<byte> bytes) {
            if (bytes.Count < 4) return null;
            int size = 4+(bytes[2]<<8)+bytes[3];
            if (size > 1028) throw new InvalidOperationException("Invalid Fast Pair frame size");
            if (bytes.Count < size) return null;
            byte[] frame = bytes.GetRange(0,size).ToArray();
            bytes.RemoveRange(0,size);
            return frame;
        }

        internal static BatteryState ParseFastPair(byte[] frame) {
            if (frame == null || (frame.Length != 5 && frame.Length != 7) || frame[0] != 3 ||
                frame[1] != 3 || frame[2] != 0 || frame[3] != frame.Length-4) return null;
            int left = frame[4]&127, right = frame.Length == 7 ? frame[5]&127 : -1;
            bool charging = (left <= 100 && (frame[4]&128) != 0) ||
                (right >= 0 && right <= 100 && (frame[5]&128) != 0);
            return Earbuds(left,right,charging);
        }

        internal static BatteryState ReadFastPair(RfcommBatteryLink link) {
            byte[] frame;
            while ((frame=link.Receive(TakeFastPairFrame)) != null) {
                var reading = ParseFastPair(frame);
                if (reading != null) return reading;
            }
            return null;
        }

        static bool IsSennheiserReply(byte[] frame, byte property) {
            return frame != null && frame.Length >= 9 && frame[0] == 0xFF && frame[1] == 3 &&
                frame[2] == 0 && frame.Length == 8+frame[3] && frame[4] == 4 &&
                frame[5] == 0x95 && frame[6] == 7 && frame[7] == property;
        }

        internal static int? ParseMomentumValue(byte[] frame, byte property) {
            if (!IsSennheiserReply(frame,property)) return null;
            if (property == 3 && frame.Length == 9 && frame[8] <= 100) return frame[8];
            if (property == 2 && frame.Length <= 10 && frame[8] <= 2) return frame[8];
            return null;
        }

        internal static BatteryState ReadMomentum(RfcommBatteryLink link) {
            link.Send(new byte[]{0xFF,3,0,0,4,0x95,6,3});
            link.Send(new byte[]{0xFF,3,0,0,4,0x95,6,2});
            int? level = null, charge = null;
            byte[] frame;
            while ((frame=link.Receive(bytes => TakeFrame(bytes,0xFF,8,data => 8+data[3]))) != null) {
                level = ParseMomentumValue(frame,3) ?? level;
                charge = ParseMomentumValue(frame,2) ?? charge;
                if (level.HasValue && charge.HasValue) break;
            }
            return level.HasValue ? ExpandedUsbBattery.Level(level.Value,charge.HasValue ? (bool?)(charge == 1) : null)
                : null;
        }

        internal static BatteryState ParseMomentumEarbuds(byte[] battery, byte[] charge) {
            if (!IsSennheiserReply(battery,3) || battery.Length < 10 || battery.Length > 11) return null;
            bool? charging = null;
            if (IsSennheiserReply(charge,2) && charge.Length == 10 && charge[8] <= 2 && charge[9] <= 2)
                charging = (battery[8] <= 100 && charge[8] == 1) || (battery[9] <= 100 && charge[9] == 1);
            return Earbuds(battery[8],battery[9],charging);
        }

        internal static BatteryState ReadMomentumEarbuds(RfcommBatteryLink link) {
            link.Send(new byte[]{0xFF,3,0,0,4,0x95,6,3});
            link.Send(new byte[]{0xFF,3,0,0,4,0x95,6,2});
            byte[] frame, battery = null, charge = null;
            while ((frame=link.Receive(bytes => TakeFrame(bytes,0xFF,8,data => 8+data[3]))) != null) {
                if (IsSennheiserReply(frame,3)) battery = frame;
                if (IsSennheiserReply(frame,2)) charge = frame;
                if (battery != null && charge != null) break;
            }
            return ParseMomentumEarbuds(battery,charge);
        }

        static byte[] TakePudding(List<byte> bytes) {
            return TakeFrame(bytes,0xFF,8,data => 8+data[3]+(data[4]<<8));
        }

        internal static BatteryState ParsePudding(byte[] frame) {
            if (frame == null || frame.Length != 14 || frame[0] != 0xFF || frame[1] != 4 ||
                frame[2] != 0 || frame[3] != 6 || frame[4] != 0 || frame[5] != 0x1D ||
                !(frame[6] == 0x1B && frame[7] == 1 || frame[6] == 0x1A && frame[7] == 0x81) ||
                frame[8] != 1 || frame[10] != 2 || frame[12] != 3) return null;
            return Earbuds(frame[9] == 0 ? -1 : frame[9],frame[11] == 0 ? -1 : frame[11]);
        }

        internal static BatteryState ReadPudding(RfcommBatteryLink link) {
            link.Send(new byte[]{0xFF,1,0,0,0,0x0A,3,0});
            byte[] frame;
            var handshake = new byte[]{0xFF,4,0,4,0,0x0A,0x83,0,0,4,3,1};
            while ((frame=link.Receive(TakePudding)) != null && !frame.SequenceEqual(handshake)) {}
            if (frame == null) return null;
            link.Send(new byte[]{0xFF,4,0,2,0,0x1D,0x1A,1,1,2});
            while ((frame=link.Receive(TakePudding)) != null) {
                var battery = ParsePudding(frame);
                if (battery != null) return battery;
            }
            return null;
        }

        internal static BatteryState ParseVivo(byte[] payload) {
            if (payload == null || payload.Length != 9 || payload[0] != 0 || payload[1] != 0x1B ||
                payload[2] != 0x82 || payload[3] != 7 || payload[4] != 0) return null;
            int left=payload[5], right=payload[6], flags=payload[8];
            return Earbuds(left,right,(left <= 100 && (flags&1) != 0) || (right <= 100 && (flags&2) != 0));
        }

        static byte[] TakeVivo(List<byte> bytes) {
            return TakeFrame(bytes,0xFF,8,data => data[2] == 0 ? 8+data[3] : 0);
        }

        internal static BatteryState ReadVivo(RfcommBatteryLink link) {
            link.Send(new byte[]{0xFF,4,0,0,0,0x0A,3,0});
            byte[] frame;
            while ((frame=link.Receive(TakeVivo)) != null) {
                if (frame[5] == 0x0A && frame[6] == 0x83 && frame[7] == 0) break;
            }
            if (frame == null) return null;
            link.Send(new byte[]{0xFF,4,0,0,0,0x1B,2,7});
            while ((frame=link.Receive(TakeVivo)) != null) {
                if (frame[1] != 3 && frame[1] != 4) continue;
                var reading = ParseVivo(frame.Skip(4).ToArray());
                if (reading != null) return reading;
            }
            return null;
        }
    }
}
