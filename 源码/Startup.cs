using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PeripheralBattery {
    public static class Startup {
        public const string TaskName="PeripheralBatteryTray";
        const string LegacyKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
        public static string LauncherPath {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"键鼠电量.exe");}}

        public static bool Enabled {
            get {
                dynamic service=null,folder=null,task=null;
                try {
                    service=Connect();folder=service.GetFolder(@"\");task=FindTask(folder);
                    return task!=null&&IsOurTask(task)&&(bool)task.Enabled;
                } catch(COMException) {return false;}
                finally {Release(task);Release(folder);Release(service);}
            }
            set {
                dynamic service=null,folder=null,existing=null,definition=null,trigger=null,action=null,registered=null;
                try {
                    service=Connect();folder=service.GetFolder(@"\");existing=FindTask(folder);
                    if(existing!=null&&!IsOurTask(existing))throw new InvalidOperationException("同名启动任务已被其他程序使用。");
                    if(!value) {
                        if(existing!=null)folder.DeleteTask(TaskName,0);
                        RemoveLegacyRun();return;
                    }
                    string sid=WindowsIdentity.GetCurrent().User.Value;
                    definition=service.NewTask(0);
                    definition.RegistrationInfo.Description="登录 Windows 后显示鼠标、键盘和耳机的电量。可在电量图标菜单中关闭。";
                    definition.Principal.UserId=sid;
                    definition.Principal.LogonType=3;
                    definition.Principal.RunLevel=0;
                    definition.Settings.Enabled=true;
                    definition.Settings.StartWhenAvailable=true;
                    definition.Settings.DisallowStartIfOnBatteries=false;
                    definition.Settings.StopIfGoingOnBatteries=false;
                    definition.Settings.RunOnlyIfNetworkAvailable=false;
                    definition.Settings.ExecutionTimeLimit="PT0S";
                    definition.Settings.MultipleInstances=2;
                    definition.Settings.AllowDemandStart=true;
                    trigger=definition.Triggers.Create(9);
                    trigger.UserId=sid;trigger.Delay="PT10S";trigger.Enabled=true;
                    action=definition.Actions.Create(0);
                    action.Path=LauncherPath;action.Arguments="--startup";
                    action.WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                    registered=folder.RegisterTaskDefinition(TaskName,definition,6,sid,null,3,null);
                    RemoveLegacyRun();
                } finally {Release(registered);Release(action);Release(trigger);Release(definition);Release(existing);Release(folder);Release(service);}
            }
        }
        static dynamic Connect() {
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            try {service.Connect();return service;}
            catch {Release(service);throw;}
        }
        static dynamic FindTask(dynamic folder) {
            try {return folder.GetTask(TaskName);}
            catch(FileNotFoundException) {return null;}
            catch(COMException error) {if(error.ErrorCode==unchecked((int)0x80070002))return null;throw;}
        }
        static bool IsOurTask(dynamic task) {
            dynamic definition=null,actions=null,action=null;
            try {
                definition=task.Definition;actions=definition.Actions;
                if((int)actions.Count!=1)return false;
                action=actions.Item(1);
                return String.Equals((string)action.Path,LauncherPath,StringComparison.OrdinalIgnoreCase);
            } finally {Release(action);Release(actions);Release(definition);}
        }
        static void RemoveLegacyRun() {
            using(var key=Registry.CurrentUser.OpenSubKey(LegacyKey,true)) {
                if(key!=null&&String.Equals(key.GetValue(TaskName) as string,"\""+LauncherPath+"\"",StringComparison.OrdinalIgnoreCase))key.DeleteValue(TaskName,false);
            }
        }
        static void Release(object value) {if(value!=null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    }
}
