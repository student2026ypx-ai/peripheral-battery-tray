using System;

namespace PeripheralBattery {
    internal static class HyzenKeyboardBattery {
        const byte Report = 4, Start = 1, End = 2, Online = 0xAA, Battery = 0x1A;
        const int OutputLength = 32;

        internal static UsbBatteryDevice Receiver() {
            return ExpandedKeyboardBattery.UniqueKeyboard(0x388D,0x0025,"Lofree Hyzen",true,
                info => info.UsagePage == 0xFF1C && info.Usage == 0x92 && info.OutputLength == OutputLength &&
                    info.InputLength >= 9 && info.InputLength <= 64,ReadPower);
        }

        static BatteryState ReadPower(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,Report,OutputLength) ||
                !HidNative.HasReport(link.Handle,0,Report,link.Info.InputLength)) return null;
            return ReadBattery(link.Write,link.ReadReply);
        }

        internal static BatteryState ReadBattery(Action<byte[]> write, Func<Func<byte[],bool>,int,byte[]> wait) {
            var online = ReadValue(write,wait,Online);
            if (!online.HasValue || online.Value == 0) return null;
            var level = ReadValue(write,wait,Battery);
            return level.HasValue ? ExpandedUsbBattery.Level(level.Value) : null;
        }

        static byte? ReadValue(Action<byte[]> write, Func<Func<byte[],bool>,int,byte[]> wait, byte command) {
            write(Request(Start));
            try {
                if (wait(reply => IsAcknowledgement(reply,Start),850) == null) return null;
                write(Request(command));
                var response = wait(reply => IsResponse(reply,command),950);
                return response == null ? (byte?)null : response[8];
            } finally {
                write(Request(End));
                wait(reply => IsAcknowledgement(reply,End),350);
            }
        }

        static byte[] Request(byte command) {
            var frame = new byte[OutputLength];
            frame[0] = Report;
            frame[3] = command;
            return frame;
        }

        static bool IsAcknowledgement(byte[] reply, byte command) {
            return reply != null && reply.Length >= 4 && reply[0] == Report && reply[3] == command;
        }

        static bool IsResponse(byte[] reply, byte command) {
            return reply != null && reply.Length >= 9 && reply[0] == Report && (reply[3] == command || reply[3] == 0) &&
                reply[5] == 0 && reply[6] == 0 && (command != Battery || (reply[8] > 0 && reply[8] <= 100));
        }
    }
}
