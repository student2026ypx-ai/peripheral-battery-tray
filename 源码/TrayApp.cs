using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

#if MOUSE_APP
[assembly:AssemblyTitle("鼠标电量")]
#elif HEADSET_APP
[assembly:AssemblyTitle("耳机电量")]
#else
[assembly:AssemblyTitle("键盘电量")]
#endif
[assembly:AssemblyProduct("键鼠电量")]
[assembly:AssemblyDescription("鼠标、键盘和耳机的电量显示")]
[assembly:AssemblyVersion("1.19.0.0")]
[assembly:AssemblyFileVersion("1.19.0.0")]

namespace PeripheralBattery {
    public static class TrayDrawing {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        public static bool LightTheme {
            get {using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return key!=null&&Convert.ToInt32(key.GetValue("SystemUsesLightTheme",0))!=0;}
        }
        public static Icon Create(string kind,int? value,bool stale) {
            return CreateStatus(kind,value,stale,false);
        }
        public static Icon CreateStatus(string kind,int? value,bool stale,bool charging) {
            using(var bitmap=RenderStatus(kind,value,stale,LightTheme,Math.Max(16,Math.Min(64,SystemInformation.SmallIconSize.Width)),charging)) {
                IntPtr handle=bitmap.GetHicon();
                try {using(var temporary=Icon.FromHandle(handle)) return (Icon)temporary.Clone();}
                finally {DestroyIcon(handle);}
            }
        }
        public static Bitmap Render(string kind,int? value,bool stale,bool lightTheme,int size) {
            return RenderStatus(kind,value,stale,lightTheme,size,false);
        }
        public static Bitmap RenderStatus(string kind,int? value,bool stale,bool lightTheme,int size,bool charging) {
            BatteryBand band=BatteryColors.Band(value,stale);
            Color color=band==BatteryBand.Unknown?(lightTheme?Color.FromArgb(112,124,137):Color.FromArgb(153,166,179)):
                band==BatteryBand.Red?(lightTheme?Color.FromArgb(189,44,57):Color.FromArgb(255,113,122)):
                band==BatteryBand.Yellow?(lightTheme?Color.FromArgb(154,99,0):Color.FromArgb(239,188,79)):
                lightTheme?Color.FromArgb(8,124,100):Color.FromArgb(37,194,155);
            var bitmap=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using(var large=new Bitmap(size*4,size*4,System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                using(var g=Graphics.FromImage(large)) {
                    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;
                    g.ScaleTransform(size*4/32f,size*4/32f);
                    var glyphTransform=g.Save();if(charging)g.TranslateTransform(-2,0);
                    DrawDeviceGlyph(g,kind,lightTheme?Color.FromArgb(44,51,61):Color.FromArgb(237,243,249));
                    g.Restore(glyphTransform);
                    if(charging) {
                        using(var power=new SolidBrush(lightTheme?Color.FromArgb(8,124,100):Color.FromArgb(37,194,155))) {
                            g.FillPolygon(power,new[]{new PointF(30.6f,2.7f),new PointF(26.7f,7.5f),new PointF(28.7f,7.5f),new PointF(27.6f,12.1f),new PointF(31.4f,6.3f),new PointF(29.4f,6.3f)});
                        }
                    }
                    using(var brush=new SolidBrush(color)) {
                        if(value.HasValue) {
                            using(var family=new FontFamily("Segoe UI"))
                            using(var format=(StringFormat)StringFormat.GenericTypographic.Clone())
                            using(var digits=new GraphicsPath()) {
                                digits.AddString(value.Value.ToString(),family,(int)FontStyle.Bold,25,PointF.Empty,format);
                                var bounds=digits.GetBounds();
                                float scale=Math.Min(10.2f/bounds.Height,25.8f/bounds.Width);
                                using(var transform=new Matrix(scale,0,0,scale,(32-bounds.Width*scale)/2-bounds.Left*scale,19.5f+(10.2f-bounds.Height*scale)/2-bounds.Top*scale))digits.Transform(transform);
                                g.FillPath(brush,digits);
                            }
                        } else {
                            using(var dash=new Pen(color,2)) {
                                dash.StartCap=dash.EndCap=LineCap.Round;
                                g.DrawLine(dash,9,24.6f,13,24.6f);g.DrawLine(dash,19,24.6f,23,24.6f);
                            }
                        }
                    }
                }
                using(var g=Graphics.FromImage(bitmap)) {
                    g.CompositingMode=CompositingMode.SourceCopy;
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.DrawImage(large,new Rectangle(0,0,size,size),0,0,large.Width,large.Height,GraphicsUnit.Pixel);
                }
            }
            return bitmap;
        }
        internal static void DrawDeviceGlyph(Graphics g,string kind,Color color) {
            using(var pen=new Pen(color,1.8f)) {
                pen.StartCap=pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                if(kind=="mouse") {
                    using(var outline=RoundedRect(11,2.9f,10,12.3f,5))g.DrawPath(pen,outline);
                    g.DrawLine(pen,16,3.8f,16,7.4f);
                    g.DrawLine(pen,11.4f,7.4f,20.6f,7.4f);
                } else if(kind=="headset") {
                    g.DrawArc(pen,7.8f,2.9f,16.4f,16.4f,180,180);
                    using(var leftCup=RoundedRect(5.6f,10.9f,4.5f,4.9f,1.65f))
                    using(var rightCup=RoundedRect(21.9f,10.9f,4.5f,4.9f,1.65f)) {
                        g.DrawPath(pen,leftCup);
                        g.DrawPath(pen,rightCup);
                    }
                } else {
                    using(var outline=RoundedRect(5,4.4f,22,10.3f,1.8f))g.DrawPath(pen,outline);
                    using(var keys=new SolidBrush(pen.Color)) {
                        for(int i=0;i<6;i++)g.FillEllipse(keys,7.9f+i*3.0f,6.9f,1.2f,1.2f);
                        for(int i=0;i<5;i++)g.FillEllipse(keys,9.3f+i*3.0f,9.4f,1.1f,1.1f);
                        using(var space=RoundedRect(11,12,10,1.3f,0.65f))g.FillPath(keys,space);
                    }
                }
            }
        }
        internal static GraphicsPath RoundedRect(float x,float y,float width,float height,float radius) {
            float d=radius*2;var path=new GraphicsPath();
            path.AddArc(x,y,d,d,180,90);path.AddArc(x+width-d,y,d,d,270,90);
            path.AddArc(x+width-d,y+height-d,d,d,0,90);path.AddArc(x,y+height-d,d,d,90,90);path.CloseFigure();return path;
        }
    }
    public sealed class DeviceView {
        public BatteryState State;
        public string DeviceKey;
        public int? LastPercent;
        public DateTime LastSeen;
        public bool Stale {get{return !State.Percent.HasValue&&LastPercent.HasValue;}}
        public int? DisplayPercent {get{return State.Percent??LastPercent;}}
        public string UnavailableReason {get{return State.Detail;}}

        public void Record(BatteryState reading) {
            if (reading.Kind == "headset" && String.IsNullOrEmpty(reading.DeviceKey)) {
                if (!String.IsNullOrEmpty(DeviceKey)) reading.Name = State.Name;
            } else if (reading.DeviceKey != DeviceKey || String.IsNullOrEmpty(reading.DeviceKey)) {
                DeviceKey = reading.DeviceKey;
                LastPercent = null;
                LastSeen = DateTime.MinValue;
            }
            State = reading;
            if (reading.Percent.HasValue) {
                LastPercent = reading.Percent;
                LastSeen = reading.CheckedAt;
            }
        }
    }
    public sealed class DeviceChangeWindow : NativeWindow,IDisposable {
        readonly Action changed;
        public DeviceChangeWindow(Action changed) {
            this.changed=changed;
            // A hidden top-level window receives the system's device-tree change broadcasts.
            CreateHandle(new CreateParams {Caption="PeripheralBattery.DeviceChange."+Program.Kind,Parent=IntPtr.Zero,ExStyle=0x80});
        }
        protected override void WndProc(ref Message message) {
            if(message.Msg==0x0219&&(message.WParam.ToInt64()==7||message.WParam.ToInt64()==0x8000||message.WParam.ToInt64()==0x8004))changed();
            base.WndProc(ref message);
        }
        public void Dispose() {DestroyHandle();}
    }
    public static class TrayDiagnostics {
        [StructLayout(LayoutKind.Sequential)] struct Identifier {public uint Size;public IntPtr Window;public uint ID;public Guid Guid;}
        [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref Identifier identifier,out Rect rect);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string title);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect rect);
        public static object Inspect(NotifyIcon icon) {
            try {
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var type=typeof(NotifyIcon);var fieldId=type.GetField("id",flags);var fieldWindow=type.GetField("window",flags);var fieldAdded=type.GetField("added",flags);
                var window=(NativeWindow)fieldWindow.GetValue(icon);
                var identifier=new Identifier {Size=(uint)Marshal.SizeOf(typeof(Identifier)),Window=window.Handle,ID=Convert.ToUInt32(fieldId.GetValue(icon))};
                Rect rect,taskbar;int result=Shell_NotifyIconGetRect(ref identifier,out rect);GetWindowRect(FindWindow("Shell_TrayWnd",null),out taskbar);
                return new {added=fieldAdded.GetValue(icon),id=identifier.ID,window=identifier.Window.ToInt64(),result=result,
                    rectangle=new[]{rect.Left,rect.Top,rect.Right,rect.Bottom},taskbar=new[]{taskbar.Left,taskbar.Top,taskbar.Right,taskbar.Bottom},lightTheme=TrayDrawing.LightTheme};
            }catch(Exception error){return new {error=error.Message,fields=typeof(NotifyIcon).GetFields(BindingFlags.NonPublic|BindingFlags.Instance).Select(f=>f.Name).ToArray()};}
        }
    }
    public sealed class TrayContext : ApplicationContext {
        readonly Control dispatcher=new Control();
        // Preserve the two existing Windows notification area IDs when falling back.
        readonly NotifyIcon[] icons=new NotifyIcon[Program.DeviceIndex+1];
        NotifyIcon ActiveIcon {get{return icons[Program.DeviceIndex];}}
        readonly DeviceView view=new DeviceView {
            State=new BatteryState {Kind=Program.Kind,Name=Program.DefaultName,Detail="正在读取…"}
        };
        readonly ContextMenuStrip menu=new ContextMenuStrip();
        readonly TaskbarBatteryDisplay display;
        readonly ToolStripMenuItem menuLine=new ToolStripMenuItem {Enabled=false};
        readonly ToolStripMenuItem startItem=new ToolStripMenuItem("开机自动显示");
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer deviceChangeTimer=new System.Windows.Forms.Timer();
        readonly DeviceChangeWindow deviceChanges;
#if KEYBOARD_APP
        readonly KeyboardBatteryEvents keyboardEvents;
#endif
        readonly EventWaitHandle refreshEvent;
        readonly RegisteredWaitHandle refreshWait;
        readonly LowBatteryAlerts lowBatteryAlerts = new LowBatteryAlerts(Path.ChangeExtension(Program.StatusPath, ".low-battery.txt"));
        DeviceBatteryPanel statusForm;
        LowBatteryPopup lowBatteryPopup;
        int busy;
        bool readingForced, forcePending;
        public TrayContext() {
            dispatcher.CreateControl();var ignored=dispatcher.Handle;
            LoadLastReading();
            menuLine.Text=Program.Label+"：正在读取";
            menu.Items.Add(menuLine);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("查看电量",null,delegate{ShowStatus();});
            menu.Items.Add("立即刷新",null,delegate{RefreshReadings();});
            startItem.CheckOnClick=false;startItem.Click+=delegate {
                try {Startup.Enabled=!Startup.Enabled;startItem.Checked=Startup.Enabled;}
                catch(Exception error) {MessageBox.Show("未能修改开机启动："+error.Message,"键鼠电量");}
            };
            menu.Items.Add(startItem);menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出",null,delegate{ExitThread();});
            menu.Opening+=delegate {startItem.Checked=Startup.Enabled;};
            for(int i=0;i<icons.Length;i++) icons[i]=new NotifyIcon();
            ActiveIcon.ContextMenuStrip=menu;
            ActiveIcon.Icon=TrayDrawing.Create(Program.Kind,view.DisplayPercent,view.Stale);
            ActiveIcon.Text=Program.Label+"电量 · 正在读取";
            ActiveIcon.MouseClick+=delegate(object sender,MouseEventArgs e) {
                if(e.Button==MouseButtons.Left) ShowStatus();
            };
            display=new TaskbarBatteryDisplay(Program.Kind,menu,ShowStatus,delegate(bool fallback){ActiveIcon.Visible=fallback;});
            display.PlacementChanged+=SaveStatus;
            display.SetReading(view.DisplayPercent,view.Stale,ActiveIcon.Text);
            refreshEvent=new EventWaitHandle(false,EventResetMode.AutoReset,BatteryRefresh.EventName(Program.Kind));
            refreshWait=ThreadPool.RegisterWaitForSingleObject(refreshEvent,delegate {if(!dispatcher.IsDisposed) try {dispatcher.BeginInvoke((Action)RefreshReadings);}catch(InvalidOperationException){}},null,-1,false);
            timer.Interval=Program.RefreshSeconds*1000;timer.Tick+=delegate{RefreshReadings(false);};timer.Start();
            deviceChangeTimer.Interval=1500;
            deviceChangeTimer.Tick+=delegate {
                if(Interlocked.CompareExchange(ref busy,0,0)!=0)return;
                deviceChangeTimer.Stop();RefreshReadings();
            };
            deviceChanges=new DeviceChangeWindow(ScheduleDeviceRefresh);
#if KEYBOARD_APP
            keyboardEvents=new KeyboardBatteryEvents(ScheduleDeviceRefresh);
#endif
            SaveStatus();
            RefreshReadings();
        }
        void ScheduleDeviceRefresh() {
            if(dispatcher.IsDisposed)return;
            try {dispatcher.BeginInvoke((Action)delegate {deviceChangeTimer.Stop();deviceChangeTimer.Start();});}
            catch(InvalidOperationException){}
        }
        void RefreshReadings() {RefreshReadings(true);}
        void RefreshReadings(bool force) {
            if(Interlocked.Exchange(ref busy,1)!=0) {
                if(force&&!readingForced)forcePending=true;
                return;
            }
            readingForced=force;
            ThreadPool.QueueUserWorkItem(delegate {
                BatteryState state;
                try {state=Program.ReadBattery(force);}
                catch(Exception error) {
                    state=new BatteryState {Kind=Program.Kind,Name=Program.DefaultName,Detail="读取失败："+error.Message};
                }
                if(dispatcher.IsDisposed) return;
                try {dispatcher.BeginInvoke((Action)delegate {
                    try {
                        if (!ReferenceEquals(state, view.State)) {
                            ApplyReading(state);
                            SaveStatus();
                        }
                        lowBatteryAlerts.Observe(state, ShowLowBattery);
                    } finally {
                        Interlocked.Exchange(ref busy,0);
                        if(forcePending) {forcePending=false;RefreshReadings();}
                    }
                });} catch(InvalidOperationException) {Interlocked.Exchange(ref busy,0);}
            });
        }
        void ApplyReading(BatteryState state) {
            view.Record(state);

            string value=view.DisplayPercent.HasValue?(state.Estimated && !view.Stale?"约 ":"")+view.DisplayPercent+"%":"未读到电量";
            string hint=Program.Label+" · "+state.Name+" · "+value;
            bool charging=state.Charging==true;
            if(view.Stale) hint+="（上次读数 "+view.LastSeen.ToString("HH:mm")+"，"+view.UnavailableReason+"）";
            if(charging) hint+=" · 充电中";
            else if(!view.Stale&&!state.Percent.HasValue) hint+=" · "+state.Detail;

            var previous=ActiveIcon.Icon;
            ActiveIcon.Icon=TrayDrawing.CreateStatus(state.Kind,view.DisplayPercent,view.Stale,charging);
            if(previous!=null) previous.Dispose();
            ActiveIcon.Text=hint.Length>63?hint.Substring(0,63):hint;
            menuLine.Text=state.Name+" · "+value+(view.Stale?" · 上次读数":"")+(charging?" · 充电中":"");
            display.SetReading(view.DisplayPercent,view.Stale,hint,charging);
        }
        bool ShowLowBattery(BatteryState state) {
            if (lowBatteryPopup != null && !lowBatteryPopup.IsDisposed) return false;
            lowBatteryPopup = LowBatteryPopup.TryShow(state, Program.Label);
            return lowBatteryPopup != null;
        }
        void SaveStatus() {
            try {
                var snapshot=new {updatedAt=DateTime.Now.ToString("o"),refreshSeconds=Program.RefreshSeconds,autostart=Startup.Enabled,display=display.Describe(),
                    tray=ActiveIcon.Visible?new[]{TrayDiagnostics.Inspect(ActiveIcon)}:new object[0],
                    devices=new[]{new {kind=view.State.Kind,name=view.State.Name,percent=view.State.Percent,lastPercent=view.LastPercent,
                        stale=view.Stale,charging=view.State.Charging,estimated=view.State.Estimated,connection=view.State.Connection,detail=view.State.Detail,deviceKey=view.DeviceKey,
                        lastSeen=view.LastSeen==DateTime.MinValue?null:view.LastSeen.ToString("o"),trayTooltip=ActiveIcon.Text}}};
                File.WriteAllText(Program.StatusPath,new JavaScriptSerializer().Serialize(snapshot),System.Text.Encoding.UTF8);
            } catch(IOException){} catch(UnauthorizedAccessException){}
        }
        void ShowStatus() {
            Rectangle anchor=new Rectangle(Cursor.Position,new Size(1,1));
            if(display.Visible) {
                var placement=TaskbarPlacement.Read();
                anchor=placement.Available&&!placement.Suppressed?placement.BoundsFor("headset"):display.Bounds;
            }
            using(var graphics=dispatcher.CreateGraphics()) {
                var opened=DeviceBatteryPanel.ShowShared(anchor,graphics.DpiX/96f);
                if(opened!=null)statusForm=opened;
            }
            RefreshReadings();
        }
        void LoadLastReading() {
            try {
                if(!File.Exists(Program.StatusPath)||new FileInfo(Program.StatusPath).Length>65536) return;
                var data=new JavaScriptSerializer().Deserialize<ReadingCache>(File.ReadAllText(Program.StatusPath));
                if(data==null||data.devices==null) return;
                foreach(var previous in data.devices) {
                    if(previous.kind!=Program.Kind||!previous.lastPercent.HasValue||previous.lastPercent<0||previous.lastPercent>100)continue;
                    if(previous.kind!="headset"||String.IsNullOrEmpty(previous.deviceKey))continue;
                    DateTime seen;if(!DateTime.TryParse(previous.lastSeen,out seen))continue;
                    view.DeviceKey=previous.deviceKey;
                    view.LastPercent=previous.lastPercent;view.LastSeen=seen;
                    if(!String.IsNullOrEmpty(previous.name))view.State.Name=previous.name;
                }
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
        }
        protected override void ExitThreadCore() {
            timer.Stop();timer.Dispose();refreshWait.Unregister(null);refreshEvent.Dispose();
            deviceChangeTimer.Stop();
            deviceChanges.Dispose();
#if KEYBOARD_APP
            keyboardEvents.Dispose();
#endif
            deviceChangeTimer.Dispose();
            if (lowBatteryPopup != null) lowBatteryPopup.Dispose();
            foreach(var icon in icons) {icon.Visible=false;var graphic=icon.Icon;icon.Dispose();if(graphic!=null)graphic.Dispose();}
            display.Dispose();menu.Dispose();if(statusForm!=null)statusForm.Dispose();dispatcher.Dispose();base.ExitThreadCore();
        }
    }
    public sealed class ReadingCache {public List<CachedDevice> devices {get;set;}}
    public sealed class CachedDevice {
        public string kind {get;set;}
        public string name {get;set;}
        public bool estimated {get;set;}
        public int? percent {get;set;}
        public int? lastPercent {get;set;}
        public bool stale {get;set;}
        public bool? charging {get;set;}
        public string detail {get;set;}
        public string lastSeen {get;set;}
        public string deviceKey {get;set;}
    }
    static class Program {
#if MOUSE_APP
        public const string Kind="mouse";
        public const string Label="鼠标";
        public const string DefaultName="鼠标";
        public const int DeviceIndex=0;
#elif KEYBOARD_APP
        public const string Kind="keyboard";
        public const string Label="键盘";
        public const string DefaultName="键盘";
        public const int DeviceIndex=1;
#elif HEADSET_APP
        public const string Kind="headset";
        public const string Label="耳机";
        public const string DefaultName=BluetoothHeadsetBattery.DefaultName;
        public const int DeviceIndex=2;
#else
#error Select a device for the battery display executable.
#endif
#if HEADSET_APP
        public const int RefreshSeconds=5;
#else
        public const int RefreshSeconds=5;
        static readonly InputBatteryMonitor InputMonitor = new InputBatteryMonitor(Kind);
#endif
        public static string StatusPath {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,Label+"电量状态.json");}}
        public static BatteryState ReadBattery(bool force) {
#if HEADSET_APP
            return BluetoothHeadsetBattery.Read(force);
#else
            return InputMonitor.Read(force);
#endif
        }
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread] static void Main(string[] args) {
            SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            if(args.Contains("--enable-autostart")) Startup.Enabled=true;
            if(args.Contains("--disable-autostart")) {Startup.Enabled=false;return;}
            bool created;
            using(var mutex=new Mutex(true,@"Local\PeripheralBatteryTray.Instance."+Kind,out created)) {
                if(!created) {BatteryRefresh.Request(Kind);return;}
                Application.Run(new TrayContext());
            }
        }
    }
}
