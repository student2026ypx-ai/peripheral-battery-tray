using System;
using System.Linq;

namespace PeripheralBattery {
    internal static class EweadnMouseDpi {
        const byte FeatureReport = 3, ReadPage = 0x4F;
        const int FeatureLength = 64, PagePartLength = 10;

        static int S9Value(int sensor, int raw) {
            switch (sensor) {
                case 17:
                    if (raw < 200) return raw*50;
                    if (raw <= 220) return 10000+(raw-200)*100;
                    return 12000+(raw-220)*1000;
                case 3:
                case 5:
                case 21:
                    return raw < 129 ? raw*50 : 6500+(raw-129)*100;
                case 37:
                case 38:
                    return raw < 50 ? raw*100 : (sensor == 37 ? 5000 : 5500)+(raw-50)*500;
                case 149:
                    return raw < 520 ? (raw+1)*50 : 26000+(raw-519)*1000;
                case 4:
                case 18:
                case 16:
                case 53:
                    return raw*100;
                default:
                    return (raw+1)*50;
            }
        }

        internal static MouseDpiState ParseS9(byte[] frame, int sensor) {
            if (frame == null || frame.Length < 8 || frame.Length > 34 || frame[0] != 0 ||
                frame[1] != 0x13 || frame[2] != 0) return null;
            int count = frame[5] & 15;
            int requiredPayload = 3+4*(count-1);
            if (count < 1 || count > 6 || frame[4] < requiredPayload || frame[4] > frame.Length-5) return null;
            var stages = new int[count];
            for (int i=0;i<count;i++)
                stages[i] = S9Value(sensor,UsbMouseSupport.LittleWord(frame,6+4*i));
            return MouseDpiQueries.Stages(frame[5]>>4,stages,(int[])stages.Clone());
        }

        internal static MouseDpiState ReadS9(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0,33) ||
                !HidNative.HasReport(link.Handle,0,0,link.Info.InputLength)) return null;
            link.Write(new byte[]{0,0x30});
            var identity = link.ReadReply(frame => MouseReceiverBattery.ParseS9(frame) != null,850);
            if (identity == null) return null;
            link.Write(new byte[]{0,0x13});
            MouseDpiState state = null;
            link.ReadReply(frame => (state = ParseS9(frame,identity[11])) != null,850);
            return state;
        }

        internal static byte[] BytechRequest(byte address, int part) {
            var frame = new byte[FeatureLength];
            frame[0] = FeatureReport;
            frame[2] = 0x50;
            frame[4] = (byte)part;
            frame[5] = ReadPage;
            frame[6] = (byte)(address+part);
            frame[1] = unchecked((byte)(frame[2]+frame[4]+frame[5]+frame[6]));
            return frame;
        }

        static byte[] ReadBytech(HidLink link, byte address, int size) {
            var data = new byte[size];
            for (int offset=0;offset<size;offset+=PagePartLength) {
                int part = offset/PagePartLength;
                int copy = Math.Min(PagePartLength,size-offset);
                var frame = UsbMouseSupport.RequestFeature(link,BytechRequest(address,part),100);
                if (frame == null || frame.Length != FeatureLength || frame[0] != FeatureReport ||
                    frame[2] != 0 || frame[3] < copy || frame[3] > FeatureLength-6 || frame[4] != part)
                    return null;
                Buffer.BlockCopy(frame,6,data,offset,copy);
            }
            return data;
        }

        internal static bool IsS9Pro(byte[] identity) {
            // The S9 Pro entry reports UUID 0x09000000000E at bytes 2–7 of page 0x80.
            return identity != null && identity.Length >= 8 && identity.Skip(2).Take(6)
                .SequenceEqual(new byte[]{9,0,0,0,0,14});
        }

        static int Bytech3395Value(int raw) { return raw < 520 ? (raw+1)*50 : raw; }

        internal static MouseDpiState ParseS9Pro(byte[] sensor, byte[] table) {
            if (sensor == null || sensor.Length < 3 || table == null || table.Length != 64 ||
                !table.Any(value => value != 0)) return null;
            int count = sensor[2]&15;
            if (count < 1 || count > 8) return null;
            bool independent = (sensor[1]&0x80) != 0;
            var x = new int[count];
            var y = new int[count];
            for (int i=0;i<count;i++) {
                x[i] = Bytech3395Value(UsbMouseSupport.LittleWord(table,1+2*i));
                y[i] = independent ? Bytech3395Value(UsbMouseSupport.LittleWord(table,41+2*i)) : x[i];
            }
            return MouseDpiQueries.Stages((sensor[2]>>4)-1,x,y);
        }

        internal static MouseDpiState ReadS9Pro(HidLink link) {
            if (!IsS9Pro(ReadBytech(link,0x80,8))) return null;
            var sensor = ReadBytech(link,0x40,10);
            return sensor == null ? null : ParseS9Pro(sensor,ReadBytech(link,0x41,64));
        }

        internal static MouseDpiState ParseX23Stage(byte[] frame) {
            return frame != null && frame.Length == 32 && frame[0] == 4 && frame[2] < 6 &&
                MouseReceiverBattery.ParseX23(frame) != null
                ? new MouseDpiState { Mode=1, Index=frame[2], CanSwitch=false } : null;
        }

        internal static MouseDpiState ReadX23Stage(HidLink link) {
            if (!HidNative.HasReport(link.Handle,0,4,32)) return null;
            MouseDpiState state = null;
            link.ReadReply(frame => (state = ParseX23Stage(frame)) != null,5500);
            return state;
        }
    }
}
