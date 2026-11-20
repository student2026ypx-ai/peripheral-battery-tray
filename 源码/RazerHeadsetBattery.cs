using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;

namespace PeripheralBattery {
    internal static class RazerHeadsetBattery {
        const byte Power = 0x21, Charge = 0x2A, Remote = 0xE1;

        internal static IEnumerable<UsbBatteryDevice> Devices() {
            yield return Pa(0x0555,"Razer BlackShark V2 Pro (2023)",false);
            yield return Pa(0x0556,"Razer BlackShark V2 Pro (2023)",false);
            yield return Pa(0x0536,"Razer Barracuda X (2022)",false);
            yield return Pa(0x053A,"Razer Barracuda Pro",true);
        }

        static UsbBatteryDevice Pa(ushort product, string name, bool barracudaPro) {
            return new UsbBatteryDevice(0x1532,product,"headset",name,true,
                info => (info.UsagePage == 0xFF00 || (!barracudaPro && info.UsagePage == 0xFF14)) &&
                    info.InputLength == 64 && info.OutputLength == 64,link => ReadPa(link,barracudaPro));
        }

        internal static byte[] PaRequest(byte command, bool barracudaPro, bool remoteOn = false) {
            bool remote = command == Remote;
            byte size = (byte)(remote ? 7 : 8);
            byte group = (byte)(remote ? 0x0E : 8);
            byte type = (byte)(remote ? 2 : 3);
            byte value = (byte)(remoteOn ? 1 : 0);
            return barracudaPro
                ? new byte[]{1,0x80,size,0x50,0x41,group,8,type,command,value}
                : new byte[]{2,0x80,size,0,0,0x50,0x41,group,0,type,command,value};
        }

        internal static int? ParsePa(byte[] frame, byte command, bool barracudaPro) {
            int cmd = barracudaPro ? 13 : 12;
            if (frame == null || frame.Length < cmd+4 || frame[0] != (barracudaPro ? 1 : 2) ||
                (barracudaPro && (frame[3] != 0x50 || frame[4] != 0x49)) || frame[cmd] != command ||
                (frame[cmd+1] != 1 && (!barracudaPro || frame[cmd+1] != 2)) ||
                frame[cmd+2] < 1 || frame[cmd+2] > 32 || cmd+3+frame[cmd+2] > frame.Length)
                return null;
            int value = frame[cmd+3];
            return value <= (command == Power ? 100 : 2) ? (int?)value : null;
        }

        static int? Query(HidLink link, byte command, bool barracudaPro) {
            for (int i=0;i<2;i++) {
                link.Write(PaRequest(command,barracudaPro));
                int? value = null;
                link.ReadReply(frame => (value = ParsePa(frame,command,barracudaPro)).HasValue,350);
                if (value.HasValue) return value;
            }
            return null;
        }

        static BatteryState ReadPa(HidLink link, bool barracudaPro) {
            byte report = (byte)(barracudaPro ? 1 : 2);
            if (!HidNative.HasReport(link.Handle,1,report,64) || !HidNative.HasReport(link.Handle,0,report,64))
                return null;
            try {
                link.Write(PaRequest(Remote,barracudaPro,true));
                Thread.Sleep(45);
                int? battery = Query(link,Power,barracudaPro);
                if (!battery.HasValue) return null;
                int? charging = Query(link,Charge,barracudaPro);
                return ExpandedUsbBattery.Level(battery.Value,charging.HasValue ? (bool?)(charging.Value == 1) : null);
            } finally {
                try { link.Write(PaRequest(Remote,barracudaPro)); }
                catch (Win32Exception) {} catch (TimeoutException) {}
            }
        }
    }
}
