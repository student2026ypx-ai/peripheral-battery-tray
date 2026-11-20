using System;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    public sealed class KeyboardBatteryEvents : IDisposable {
        readonly ManualResetEvent stop=new ManualResetEvent(false);
        readonly Thread worker;
        readonly Action changed;
        public KeyboardBatteryEvents(Action changed) {
            this.changed=changed;
            worker=new Thread(Listen) {IsBackground=true,Name="Keyboard battery notifications"};worker.Start();
        }
        void Listen() {
            while(!stop.WaitOne(0)) {
                try {
                    var info=HidNative.Enumerate().Where(KeyboardProtocol.IsChannel).OrderBy(d=>d.Pid==0x2015?0:1).FirstOrDefault();
                    if(info==null) {stop.WaitOne(2000);continue;}
                    using(var link=new HidLink(info)) {
                        int previous=-1;bool? wasCharging=null;
                        while(!stop.WaitOne(0)) {
                            var raw=link.Read(500);int percent;bool charging;
                            if(!KeyboardProtocol.TryBatteryEvent(raw,out percent,out charging))continue;
                            if(previous==percent&&wasCharging==charging)continue;
                            previous=percent;wasCharging=charging;changed();
                        }
                    }
                }catch(Exception) {if(stop.WaitOne(1000))return;}
            }
        }
        public void Dispose() {stop.Set();if(worker.Join(2000))stop.Dispose();}
    }
}
