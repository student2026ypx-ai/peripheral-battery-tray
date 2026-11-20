using System;
using System.Linq;

namespace PeripheralBattery {
    internal enum CompxDpiEncoding { Paw3955, Vgn50, Paw3395Se }

    internal static class CompxMouseDpi {
        const int ReportLength = 17;
        static readonly byte[] Invalid3395SeCodes = {
            7,13,20,26,33,40,46,53,60,66,73,80,86,93,100,106,113,120,126,133,140,146,
            153,160,166,173,180,186,193,200,206,213,220,226,233
        };

        static bool ValidChecksum(byte[] bytes) {
            return bytes != null && (bytes.Sum(value => (int)value) & 255) == 0x55;
        }

        static bool ValidFrame(byte[] frame, byte command) {
            return frame != null && frame.Length == ReportLength && frame[0] == 8 &&
                frame[1] == command && frame[2] == 0 && ValidChecksum(frame);
        }

        static bool HasTransport(HidLink link) {
            return HidNative.HasReport(link.Handle,1,8,ReportLength) &&
                HidNative.HasReport(link.Handle,0,8,ReportLength);
        }

        internal static bool IsRegisterReply(byte[] frame, byte[] request) {
            return (ValidFrame(frame,8) || ValidFrame(frame,7)) && frame[3] == request[3] &&
                frame[4] == request[4] && frame[5] == request[5] && !frame.SequenceEqual(request);
        }

        static byte[] ReadRegister(HidLink link, int address, byte count) {
            var request = DareuMouse.BuildRequest(8,address,count);
            link.Write(request);
            var frame = link.ReadReply(reply => IsRegisterReply(reply,request),500);
            if (frame == null) return null;
            var data = new byte[count];
            Buffer.BlockCopy(frame,6,data,0,count);
            return data;
        }

        static bool ValidSystem(byte[] data) {
            return data != null && data.Length == 6 && data[2] >= 1 && data[2] <= 8 && data[4] < data[2] &&
                ((data[2]+data[3]) & 255) == 0x55 && ((data[4]+data[5]) & 255) == 0x55;
        }

        static int VgnAxis(byte low, int mode) {
            int steps = low | ((mode >> 2) << 8);
            int multiplier = 1 << ((mode & 1) + ((mode >> 1) & 1));
            return (steps+1)*50*multiplier;
        }

        internal static int R1Axis(byte code, int mode) {
            if (code == 0 || code > 235 || (mode != 0 && mode != 2) || Invalid3395SeCodes.Contains(code))
                return 0;
            int dpi = (code-Invalid3395SeCodes.Count(value => value < code))*50;
            if (mode == 2) return dpi > 5000 ? dpi*2 : 0;
            return dpi;
        }

        internal static MouseDpiState ParseStages(byte[] system, byte[][] rows, CompxDpiEncoding encoding) {
            if (!ValidSystem(system) || rows == null || rows.Length != system[2]) return null;
            var x = new int[rows.Length];
            var y = new int[x.Length];
            int rowSize = encoding == CompxDpiEncoding.Paw3955 ? 6 : 4;
            for (int i=0;i<rows.Length;i++) {
                var row = rows[i];
                if (row == null || row.Length != rowSize || !ValidChecksum(row)) return null;
                if (encoding == CompxDpiEncoding.Paw3955) {
                    x[i] = (UsbMouseSupport.LittleWord(row,0)+1)*((row[4]&1) == 0 ? 1 : 2);
                    y[i] = (UsbMouseSupport.LittleWord(row,2)+1)*((row[4]&16) == 0 ? 1 : 2);
                } else if (encoding == CompxDpiEncoding.Paw3395Se) {
                    x[i] = R1Axis(row[0],row[2]&15);
                    y[i] = R1Axis(row[1],row[2]>>4);
                } else {
                    x[i] = VgnAxis(row[0],row[2]&15);
                    y[i] = VgnAxis(row[1],row[2]>>4);
                }
            }
            return MouseDpiQueries.Stages(system[4],x,y);
        }

        static MouseDpiState ReadStages(HidLink link, CompxDpiEncoding encoding) {
            var system = ReadRegister(link,0,6);
            if (!ValidSystem(system)) return null;
            int address = encoding == CompxDpiEncoding.Paw3955 ? 0x1B00 : 0x000C;
            byte rowSize = encoding == CompxDpiEncoding.Paw3955 ? (byte)6 : (byte)4;
            var rows = new byte[system[2]][];
            for (int i=0;i<rows.Length;i++) {
                rows[i] = ReadRegister(link,address+i*rowSize,rowSize);
                if (rows[i] == null) return null;
            }
            var updated = ReadRegister(link,0,6);
            return ValidSystem(updated) && updated[2] == system[2]
                ? ParseStages(updated,rows,encoding) : null;
        }

        internal static MouseDpiState ReadAtkA9(HidLink link) {
            if (!HasTransport(link)) return null;
            link.Write(DareuMouse.BuildRequest(1));
            if (link.ReadReply(frame => ValidFrame(frame,1),500) == null) return null;
            return ReadStages(link,CompxDpiEncoding.Paw3955);
        }

        internal static MouseDpiState ReadVgn(HidLink link) {
            if (!HasTransport(link)) return null;
            link.Write(DareuMouse.BuildRequest(3));
            if (link.ReadReply(frame => ValidFrame(frame,3) && frame[6] == 1,500) == null) return null;
            return ReadStages(link,CompxDpiEncoding.Vgn50);
        }

        internal static bool IsR1SePlus(byte[] frame) {
            return ValidFrame(frame,0x10) && frame[5] >= 2 && frame[5] <= 10 &&
                frame[6] == 2 && frame[7] == 32;
        }

        internal static MouseDpiState ReadR1(HidLink link) {
            if (!HasTransport(link)) return null;
            link.Write(DareuMouse.BuildRequest(0x10));
            if (link.ReadReply(IsR1SePlus,550) == null) return null;
            return ReadStages(link,CompxDpiEncoding.Paw3395Se);
        }
    }
}
