using System;
using System.Linq;

namespace PeripheralBattery {
    internal static class MouseDpiQueries {
        internal static MouseDpiState Current(int x, int y) {
            if (x < 1 || x > ushort.MaxValue || y < 1 || y > ushort.MaxValue) return null;
            return new MouseDpiState { Mode=1, Current=x, CurrentVertical=y, CanSwitch=false };
        }

        internal static MouseDpiState Stages(int active, int[] x, int[] y) {
            if (x == null || y == null || x.Length != y.Length || x.Length > 8 ||
                active < 0 || active >= x.Length || x.Concat(y).Any(value => value < 1 || value > ushort.MaxValue))
                return null;
            return new MouseDpiState { Mode=1, Current=x[active], CurrentVertical=y[active], Index=active,
                Presets=x, VerticalPresets=y, CanSwitch=false };
        }

        internal static MouseDpiState ParseRapoo(byte[] frame) {
            return frame != null && frame.Length >= 9 && frame[0] == 7
                ? Current(UsbMouseSupport.LittleWord(frame,3),UsbMouseSupport.LittleWord(frame,5)) : null;
        }

        internal static MouseDpiState ReadRapoo(HidLink link) {
            if (!HidNative.HasReport(link.Handle,0,7,link.Info.InputLength)) return null;
            MouseDpiState state = null;
            link.ReadReply(frame => (state = ParseRapoo(frame)) != null,3600);
            return state;
        }

        internal static MouseDpiState ReadRog(HidLink link, bool separateAxes, int minimumDpi, int maximumDpi) {
            if (!HidNative.HasReport(link.Handle,1,0,65) || !HidNative.HasReport(link.Handle,0,0,65))
                return null;
            var state = AsusAimPointMouse.ReadDpi(link,separateAxes,minimumDpi,maximumDpi);
            // The models registered here have four firmware slots with a fixed stage count.
            if (state != null) state.ConfirmedStages = 0;
            return state;
        }

        internal static MouseDpiState ParseMchose(byte[] frame) {
            if (!MouseReceiverBattery.Command65(frame,0xAA,0x0E) || frame[12] < 1 || frame[12] > 6)
                return null;
            var stages = new int[frame[12]];
            for (int i=0;i<stages.Length;i++) {
                stages[i] = UsbMouseSupport.LittleWord(frame,14+2*i);
                if (stages[i] < 50 || stages[i] > 50000) return null;
            }
            return Stages(frame[13]-1,stages,(int[])stages.Clone());
        }

        internal static MouseDpiState ReadMchose(HidLink link) {
            if (!HidNative.HasReport(link.Handle,1,0,65) || !HidNative.HasReport(link.Handle,0,0,65))
                return null;
            link.Write(new byte[]{0,0x55,0xED});
            if (link.ReadReply(MouseReceiverBattery.MchoseOnline,550) == null) return null;
            link.Write(new byte[]{0,0x55,0x0E,0xA5,0x0B,0x30,1,1,1});
            MouseDpiState state = null;
            link.ReadReply(frame => (state = ParseMchose(frame)) != null,850);
            return state;
        }
    }
}
