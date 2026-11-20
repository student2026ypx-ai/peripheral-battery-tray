using System;
using System.Linq;

namespace PeripheralBattery {
    public static class KeyboardProtocol {
        public static bool IsChannel(HidInfo info) {
            bool product=(info.Pid==0x3033||info.Pid==0x2015)&&info.Product!=null&&info.Product.IndexOf("G87",StringComparison.OrdinalIgnoreCase)>=0;
            return info.Transport=="usb"&&info.Vid==0x3837&&product&&info.InputLength==65&&info.OutputLength==65&&
                ((info.UsagePage==1&&info.Usage==0)||(info.UsagePage==0xFF00&&info.Usage==1));
        }
        public static byte[] BuildRead(byte command,ushort offset,int length) {
            if((command!=4&&command!=5)||length<65)throw new ArgumentException("Only the known keyboard read commands are allowed");
            var report=new byte[length];report[1]=0x55;report[2]=command;
            report[4]=(byte)((56+(offset&255)+(offset>>8))&255);report[5]=56;report[6]=(byte)offset;report[7]=(byte)(offset>>8);
            return report;
        }
        public static byte[] Payload(byte[] raw) {return raw!=null&&raw.Length>0&&raw[0]==0?raw.Skip(1).ToArray():raw;}
        public static bool TryResponse(byte[] raw,byte command,ushort offset,out byte[] payload) {
            payload=null;var data=Payload(raw);
            if((command!=4&&command!=5)||data==null||data.Length<8||data[0]!=0xAA||data[1]!=command||data[2]!=0||
                data[4]>56||data[5]!=(byte)offset||data[6]!=(byte)(offset>>8)||data.Length<8+data[4])return false;
            int checksum=0;for(int i=4;i<8+data[4];i++)checksum+=data[i];
            if((byte)checksum!=data[3])return false;
            payload=data.Skip(8).Take(data[4]).ToArray();return true;
        }
        public static bool IsRejected(byte[] raw,byte command) {
            var data=Payload(raw);return data!=null&&data.Length>=2&&data[0]==0xAB&&data[1]==command;
        }
        public static byte Profile(byte[] profiles) {
            if(profiles!=null&&profiles.Length>=6&&profiles[1]>0&&profiles[1]<=4&&profiles[0]<profiles[1]&&profiles[2+profiles[0]]<4)return profiles[2+profiles[0]];
            return 0;
        }
        public static bool TryPower(byte[] config,out int percent,out bool charging) {
            percent=0;charging=false;
            if(config==null||config.Length<=32)return false;
            int packed=config[32];percent=packed&0x7F;charging=(packed&0x80)!=0;return percent<=100;
        }
        public static bool TryBatteryEvent(byte[] raw,out int percent,out bool charging) {
            percent=0;charging=false;var data=Payload(raw);
            if(data==null||data.Length<5||data[0]!=0xFA||data[1]!=0xFB||data[2]!=7||data[3]>100||data[4]>1)return false;
            percent=data[3];charging=data[4]==1;return true;
        }
    }
}
