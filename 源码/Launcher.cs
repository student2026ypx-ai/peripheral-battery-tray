using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using PeripheralBattery;
[assembly:AssemblyTitle("键鼠电量")]
[assembly:AssemblyDescription("启动鼠标、键盘和耳机电量显示")]
[assembly:AssemblyProduct("键鼠电量")]
[assembly:AssemblyVersion("1.2.0.0")]
class Launcher {
    static void Log(string message) {
        try {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"启动记录.txt");
            if(File.Exists(path)&&new FileInfo(path).Length>32768)File.WriteAllText(path,"",Encoding.UTF8);
            File.AppendAllText(path,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" "+message+Environment.NewLine,Encoding.UTF8);
        } catch(IOException){}catch(UnauthorizedAccessException){}
    }
    [STAThread] static int Main(string[] args) {
        string folder=AppDomain.CurrentDomain.BaseDirectory;
        if(Array.IndexOf(args,"--enable-autostart")>=0||Array.IndexOf(args,"--disable-autostart")>=0) {
            try {
                bool enable=Array.IndexOf(args,"--enable-autostart")>=0;
                Startup.Enabled=enable;Log(enable?"已启用登录后自动显示":"已关闭登录后自动显示");return 0;
            } catch(Exception error) {Log("配置启动失败："+error);return 1;}
        }
        bool automatic=Array.IndexOf(args,"--startup")>=0;
        Log(automatic?"自动启动流程开始":"手动启动流程开始");
        int failures=0;
        foreach(string file in new[]{"鼠标电量.exe","键盘电量.exe","耳机电量.exe"}) {
            try {
                var start=new ProcessStartInfo(Path.Combine(folder,file));start.UseShellExecute=false;start.CreateNoWindow=true;start.WindowStyle=ProcessWindowStyle.Hidden;start.WorkingDirectory=folder;
                using(var process=Process.Start(start))Log(file+" 已启动，进程 "+process.Id);
            } catch(Exception error) {failures++;Log(file+" 启动失败："+error.Message);}
        }
        if(failures>0&&!automatic)MessageBox.Show("部分电量图标未能启动，详情见同目录的启动记录.txt。","键鼠电量");
        return failures==0?0:1;
    }
}
