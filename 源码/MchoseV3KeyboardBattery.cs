using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    internal static class MchoseV3KeyboardBattery {
        const byte QhwReport = 0x4D;
        static int sequence = Environment.TickCount;

        internal static IEnumerable<UsbBatteryDevice> Devices() {
            const string g98 = "MCHOSE G98 V3 2.4G", k99 = "MCHOSE K99 V3 2.4G";
            yield return ExpandedKeyboardBattery.UniqueKeyboard(0x3837,0x1015,g98,true,
                info => ExpandedKeyboardBattery.ProductIs(info,g98) && info.UsagePage == 0xFF80 && info.Usage == 1 &&
                    info.InputLength >= 11 && info.OutputLength >= 9,ReadG98);
            yield return ExpandedKeyboardBattery.UniqueKeyboard(0x3837,0x3033,k99,true,
                info => ExpandedKeyboardBattery.ProductIs(info,k99) && info.UsagePage == 1 && info.Usage == 0 &&
                    ExpandedUsbBattery.OnInterface(info,1) && info.InputLength == 65 && info.OutputLength == 65 &&
                    (info.PhysicalId ?? "").StartsWith(@"USB\VID_3837&PID_3033\",StringComparison.OrdinalIgnoreCase),
                link => ExpandedUsbBattery.Ask(link,new byte[]{0,0x55,3,0,0x38,0x38},ParseK99,1000,0));
        }

        internal static byte[] G98Request(byte token) {
            return new byte[]{QhwReport,1,3,0,4,9,0,token,(byte)(0x0E ^ token)};
        }

        static BatteryState ReadG98(HidLink link) {
            byte token = unchecked((byte)Interlocked.Increment(ref sequence));
            return ExpandedUsbBattery.Ask(link,G98Request(token),
                frame => frame.Length == link.Info.InputLength ? ParseG98(frame,token) : null,1000,QhwReport);
        }

        internal static BatteryState ParseG98(byte[] frame, byte token) {
            if (frame == null || frame.Length < 11 || frame[0] != QhwReport || frame[1] != 1 || frame[2] != 3 ||
                frame[3] != 2 || frame[4] != 4 || frame[5] != 9 || frame[6] != 0 || frame[7] != token ||
                frame[8] > 2 || frame[9] > 100) return null;
            return ExpandedUsbBattery.Level(frame[9],frame[8] == 1);
        }

        internal static BatteryState ParseK99(byte[] frame) {
            if (frame == null || frame.Length != 65 || frame[0] != 0 || frame[1] != 0xAA ||
                frame[2] != 3 || frame[9] > 100 || !frame.Skip(3).Any(value => value != 0)) return null;
            return ExpandedUsbBattery.Level(frame[9]);
        }
    }
}
