using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PeripheralBattery {
    public sealed class TaskbarPlacement {
        const int DisplayCount=3;
        [StructLayout(LayoutKind.Sequential)] struct NativeRect {public int Left,Top,Right,Bottom;public Rectangle Rectangle {get{return Rectangle.FromLTRB(Left,Top,Right,Bottom);}}}
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string title);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string title);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int max);
        public bool Available,Suppressed;
        public Rectangle Taskbar,NotificationArea;
        public int SmallIcon,DisplaySize,Gap,RightPadding;
        int FirstLeft {get{return NotificationArea.Left-RightPadding-DisplayCount*DisplaySize-(DisplayCount-1)*Gap;}}
        public static TaskbarPlacement Read() {
            var result=new TaskbarPlacement();
            result.SmallIcon=Math.Max(16,SystemInformation.SmallIconSize.Width);
            result.DisplaySize=result.SmallIcon*2;result.Gap=Math.Max(6,result.SmallIcon*3/8);result.RightPadding=Math.Max(8,result.SmallIcon/2);
            IntPtr taskbar=FindWindow("Shell_TrayWnd",null);
            IntPtr notification=FindWindowEx(taskbar,IntPtr.Zero,"TrayNotifyWnd",null);
            NativeRect bar,notify;
            if(taskbar==IntPtr.Zero||notification==IntPtr.Zero||!GetWindowRect(taskbar,out bar)||!GetWindowRect(notification,out notify))return result;
            result.Taskbar=bar.Rectangle;result.NotificationArea=notify.Rectangle;
            if(result.Taskbar.Width<=result.Taskbar.Height||result.Taskbar.Height<result.DisplaySize+2||result.NotificationArea.Width<=0)return result;
            var screen=Screen.FromRectangle(result.Taskbar).Bounds;
            if(!IsWindowVisible(taskbar)||Rectangle.Intersect(screen,result.Taskbar).Height<result.DisplaySize) {result.Available=true;result.Suppressed=true;return result;}
            result.Available=result.FirstLeft>=result.Taskbar.Left;
            IntPtr foreground=GetForegroundWindow();
            if(foreground!=IntPtr.Zero&&foreground!=taskbar) {
                var cls=new StringBuilder(128);GetClassName(foreground,cls,cls.Capacity);
                NativeRect rect;
                if(cls.ToString()!="Progman"&&cls.ToString()!="WorkerW"&&cls.ToString()!="Shell_SecondaryTrayWnd"&&GetWindowRect(foreground,out rect)) {
                    var full=rect.Rectangle;
                    result.Suppressed=full.Left<=screen.Left+1&&full.Top<=screen.Top+1&&full.Right>=screen.Right-1&&full.Bottom>=screen.Bottom-1;
                }
            }
            return result;
        }
        public Rectangle BoundsFor(string kind) {
            int index=kind=="headset"?2:kind=="keyboard"?1:0;
            return new Rectangle(FirstLeft+index*(DisplaySize+Gap),
                Taskbar.Top+(Taskbar.Height-DisplaySize)/2,DisplaySize,DisplaySize);
        }
        public object Describe() {return new {available=Available,suppressed=Suppressed,smallIconPixels=SmallIcon,displayPixels=DisplaySize,scale=2,
            taskbar=new[]{Taskbar.Left,Taskbar.Top,Taskbar.Right,Taskbar.Bottom},notificationArea=new[]{NotificationArea.Left,NotificationArea.Top,NotificationArea.Right,NotificationArea.Bottom}};}
    }

    public sealed class TaskbarBatteryDisplay : Form {
        [StructLayout(LayoutKind.Sequential)] struct NativePoint {public int X,Y;public NativePoint(int x,int y){X=x;Y=y;}}
        [StructLayout(LayoutKind.Sequential)] struct NativeSize {public int Width,Height;public NativeSize(int width,int height){Width=width;Height=height;}}
        [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend {public byte Operation,Flags,Alpha,Format;}
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr bitmap);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window,IntPtr screen,ref NativePoint destination,ref NativeSize size,IntPtr memory,ref NativePoint source,int key,ref Blend blend,int flags);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        readonly string kind;
        readonly Action<bool> setFallback;
        readonly System.Windows.Forms.Timer placementTimer=new System.Windows.Forms.Timer();
        readonly ToolTip tooltip=new ToolTip {InitialDelay=400,AutoPopDelay=8000,ShowAlways=true};
        int? percent;
        bool stale,charging,dirty=true;
        bool? fallback,lastTheme;
        TaskbarPlacement placement;
        string error;
        public event Action PlacementChanged;
        public TaskbarBatteryDisplay(string kind,ContextMenuStrip menu,Action showDetails,Action<bool> setFallback) {
            this.kind=kind;this.setFallback=setFallback;
            Text=kind=="mouse"?"游戏鼠标电量显示":Program.Label+"电量显示";
            FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;AutoScaleMode=AutoScaleMode.None;
            StartPosition=FormStartPosition.Manual;AccessibleName=Text;
            MouseUp+=delegate(object sender,MouseEventArgs e) {
                if(e.Button==MouseButtons.Left)showDetails();
                else if(e.Button==MouseButtons.Right)menu.Show(this,e.Location);
            };
            placementTimer.Interval=1000;placementTimer.Tick+=delegate {ApplyPlacement(true);};placementTimer.Start();
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {
            get {var cp=base.CreateParams;cp.ExStyle|=0x00080000|0x08000000|0x00000080;return cp;}
        }
        public void SetReading(int? value,bool stale,string hint,bool charging=false) {
            this.percent=value;this.stale=stale;this.charging=charging&&!stale;dirty=true;
            tooltip.SetToolTip(this,hint);AccessibleDescription=hint;
            ApplyPlacement(false);
        }
        void UseFallback(bool value) {
            if(fallback!=value) {fallback=value;setFallback(value);}
        }
        void ApplyPlacement(bool notify) {
            if(IsDisposed)return;
            placement=TaskbarPlacement.Read();
            if(!placement.Available) {Hide();UseFallback(true);return;}
            UseFallback(false);
            if(placement.Suppressed) {Hide();return;}
            Rectangle bounds=placement.BoundsFor(kind);
            bool theme=TrayDrawing.LightTheme;
            bool changed=Bounds!=bounds||lastTheme!=theme||!Visible;
            try {
                if(Bounds!=bounds)Bounds=bounds;
                if(dirty||changed) {DrawSurface(theme);dirty=false;lastTheme=theme;}
                if(!Visible)Show();
                SetWindowPos(Handle,new IntPtr(-1),bounds.X,bounds.Y,bounds.Width,bounds.Height,0x0010|0x0200);
                error=null;
            }catch(Win32Exception failure) {error=failure.Message;Hide();UseFallback(true);}
            if(notify&&changed&&PlacementChanged!=null)PlacementChanged();
        }
        void DrawSurface(bool lightTheme) {
            using(var source=TrayDrawing.RenderStatus(kind,percent,stale,lightTheme,Width,charging))
            using(var frame=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb)) {
                // Alpha 1 keeps the whole display clickable while remaining visually transparent.
                using(var g=Graphics.FromImage(frame)) {g.Clear(Color.FromArgb(1,255,255,255));g.DrawImageUnscaled(source,0,0);}
                IntPtr screen=GetDC(IntPtr.Zero),memory=CreateCompatibleDC(screen),bitmap=IntPtr.Zero,previous=IntPtr.Zero;
                try {
                    bitmap=frame.GetHbitmap(Color.FromArgb(0));previous=SelectObject(memory,bitmap);
                    var destination=new NativePoint(Left,Top);var origin=new NativePoint(0,0);var size=new NativeSize(Width,Height);
                    var blend=new Blend {Alpha=255,Format=1};
                    if(!UpdateLayeredWindow(Handle,screen,ref destination,ref size,memory,ref origin,0,ref blend,2))throw new Win32Exception(Marshal.GetLastWin32Error());
                }finally {
                    if(previous!=IntPtr.Zero)SelectObject(memory,previous);
                    if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);
                    DeleteDC(memory);ReleaseDC(IntPtr.Zero,screen);
                }
            }
        }
        public object Describe() {return new {mode=fallback==true?"tray-fallback":"taskbar",visible=Visible,chargingShown=charging,
            rectangle=new[]{Left,Top,Right,Bottom},placement=placement==null?null:placement.Describe(),background="transparent",error=error};}
        protected override void Dispose(bool disposing) {
            if(disposing) {placementTimer.Stop();placementTimer.Dispose();tooltip.Dispose();}
            base.Dispose(disposing);
        }
    }
}
