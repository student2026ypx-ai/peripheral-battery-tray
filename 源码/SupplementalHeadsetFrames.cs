using System;
using System.Collections.Generic;

namespace PeripheralBattery {
    internal static class SupplementalHeadsetFrames {
        internal static ushort NothingCrc(byte[] frame, int length) {
            ushort crc = 0xFFFF;
            for (int index=0;index<length;index++) {
                crc ^= frame[index];
                for (int bit=0;bit<8;bit++)
                    crc = (ushort)((crc>>1)^((crc&1) == 0 ? 0 : 0xA001));
            }
            return crc;
        }

        internal static byte[] NothingQuery(byte command, byte sequence) {
            var frame = new byte[]{0x55,0x60,1,command,0xC0,0,0,sequence,0,0};
            ushort crc = NothingCrc(frame,8);
            frame[8] = (byte)crc;
            frame[9] = (byte)(crc>>8);
            return frame;
        }

        static byte[] TakeNothing(List<byte> bytes) {
            return BluetoothHeadsetFrames.TakeFrame(bytes,0x55,8,data => 10+data[5]+(data[6]<<8));
        }

        static bool IsNothingFrame(byte[] frame) {
            return frame != null && frame.Length >= 10 && frame[0] == 0x55 &&
                (frame[1] == 0x20 || frame[1] == 0x60) && frame[2] == 1 &&
                frame.Length == 10+UsbMouseSupport.LittleWord(frame,5) &&
                NothingCrc(frame,frame.Length-2) == UsbMouseSupport.LittleWord(frame,frame.Length-2);
        }

        internal static bool IsNothingEarModel(byte[] frame) {
            return IsNothingFrame(frame) && frame.Length == 12 && frame[3] == 0x1C && frame[4] == 0x40 &&
                frame[8] == 0x71 && frame[9] == 0xB1;
        }

        internal static BatteryState ParseNothingBattery(byte[] frame) {
            if (!IsNothingFrame(frame) || frame.Length < 13 ||
                !(frame[3] == 7 && frame[4] == 0x40 || frame[3] == 1 && frame[4] == 0xE0) ||
                frame[8] < 1 || frame[8] > 3 || frame.Length != 11+2*frame[8]) return null;
            int left = -1, right = -1;
            bool charging = false;
            for (int index=9;index<frame.Length-2;index+=2) {
                int part = frame[index], level = frame[index+1]&0x7F;
                if (part != 2 && part != 3 || level > 100) continue;
                if (part == 2) left = level;
                else right = level;
                charging |= (frame[index+1]&0x80) != 0;
            }
            return BluetoothHeadsetFrames.Earbuds(left,right,charging);
        }

        internal static BatteryState ReadNothingEar(RfcommBatteryLink link) {
            link.Send(NothingQuery(0x1C,1));
            byte[] frame;
            while ((frame=link.Receive(TakeNothing)) != null) {
                if (IsNothingEarModel(frame)) break;
            }
            if (frame == null) return null;
            System.Threading.Thread.Sleep(40);
            link.Send(NothingQuery(7,2));
            while ((frame=link.Receive(TakeNothing)) != null) {
                var level = ParseNothingBattery(frame);
                if (level != null) return level;
            }
            return null;
        }

        internal static BatteryState ParseSpaceOne(byte[] frame) {
            if (frame == null || frame.Length != 105 || frame[0] != 9 || frame[1] != 0xFF ||
                frame[2] != 0 || frame[3] != 0 || frame[4] != 1 || frame[5] != 1 || frame[6] != 1 ||
                UsbMouseSupport.LittleWord(frame,7) != frame.Length || frame[9] > 5) return null;
            byte sum = 0;
            for (int index=0;index<frame.Length-1;index++) sum = unchecked((byte)(sum+frame[index]));
            if (sum != frame[frame.Length-1]) return null;
            return ExpandedUsbBattery.Level(frame[9]*20,frame[10] <= 1 ? (bool?)(frame[10] == 1) : null,true);
        }

        internal static BatteryState ReadSpaceOne(RfcommBatteryLink link) {
            link.Send(new byte[]{8,0xEE,0,0,0,1,1,10,0,2});
            byte[] frame;
            while ((frame=link.Receive(bytes => BluetoothHeadsetFrames.TakeFrame(bytes,9,9,
                    data => data[7]+(data[8]<<8)))) != null) {
                var level = ParseSpaceOne(frame);
                if (level != null) return level;
            }
            return null;
        }
    }
}
