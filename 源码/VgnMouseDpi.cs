using System.Threading;

namespace PeripheralBattery {
    internal static class VgnMouseDpi {
        const int RadioLength = 65;
        const byte Status = 0xF7, SelectMouse = 0xF6, Release = 0xFC, Config = 0x9F, DpiTable = 0x90;

        internal static MouseDpiState ParseSe(byte[] frame) {
            if (frame == null || frame.Length != RadioLength || frame[0] != 0 || frame[2] != 0x0E ||
                frame[12] < 1 || frame[12] > 8) return null;
            var stages = new int[frame[12]];
            for (int i=0;i<stages.Length;i++) stages[i] = UsbMouseSupport.LittleWord(frame,14+2*i);
            return MouseDpiQueries.Stages(frame[13]-1,stages,(int[])stages.Clone());
        }

        internal static MouseDpiState ReadSe(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0,RadioLength) ||
                !HidNative.HasReport(link.Handle,0,0,RadioLength)) return null;
            link.Write(new byte[]{0,0x55,0x0E});
            MouseDpiState state = null;
            link.ReadReply(frame => (state = ParseSe(frame)) != null,900);
            return state;
        }

        internal static byte[] RyRequest(byte command, byte argument = 0) {
            var frame = new byte[RadioLength];
            frame[1] = command;
            frame[2] = argument;
            frame[8] = unchecked((byte)(0xFF-command-argument));
            return frame;
        }

        static byte[] RyStatus(HidLink link) {
            var frame = UsbMouseSupport.RequestFeature(link,RyRequest(Status),60);
            return frame != null && frame.Length == RadioLength && frame[0] == 0 && frame[1] <= 1 &&
                frame[5] == 0 && frame[6] <= 1 ? frame : null;
        }

        static byte[] WaitForFlag(HidLink link, int index) {
            for (int attempt=0;attempt<4;attempt++) {
                var status = RyStatus(link);
                if (status == null) return null;
                if (status[index] == 1) return status;
                Thread.Sleep(40);
            }
            return null;
        }

        static byte[] ReadRyPage(HidLink link, byte command, byte argument = 0) {
            if (WaitForFlag(link,6) == null || !link.WriteFeature(RyRequest(command,argument)))
                return null;
            Thread.Sleep(100);
            if (WaitForFlag(link,1) == null) return null;
            var frame = UsbMouseSupport.RequestFeature(link,RyRequest(Release),60);
            return frame != null && frame.Length == RadioLength && frame[0] == 0 && frame[1] == command
                ? frame : null;
        }

        internal static MouseDpiState ParseRy(byte[] frame, byte profile) {
            if (frame == null || frame.Length != RadioLength || frame[0] != 0 || frame[1] != DpiTable ||
                frame[2] != profile || frame[4] < 1 || frame[4] > 8) return null;
            var x = new int[frame[4]];
            var y = new int[x.Length];
            for (int i=0;i<x.Length;i++) {
                x[i] = UsbMouseSupport.LittleWord(frame,9+2*i);
                y[i] = UsbMouseSupport.LittleWord(frame,25+2*i);
            }
            return MouseDpiQueries.Stages(frame[3],x,y);
        }

        internal static MouseDpiState ReadRy(HidLink link) {
            if (WaitForFlag(link,6) == null ||
                !link.WriteFeature(RyRequest(SelectMouse,5))) return null;
            Thread.Sleep(70);
            var config = ReadRyPage(link,Config);
            if (config == null || config[9] > 3) return null;
            return ParseRy(ReadRyPage(link,DpiTable,config[9]),config[9]);
        }
    }
}
