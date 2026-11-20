# 键鼠耳机电量

一个 Windows 任务栏小工具，用来查看无线鼠标、键盘和耳机的电量。点击任一电量图标，可在设备电量和快捷控制之间切换。

## 下载和使用

1. 到 [v1.19 测试版](https://github.com/student2026ypx-ai/peripheral-battery-tray/releases/tag/v1.19) 下载 Windows x64 ZIP。
2. 解压到可写目录，双击 `键鼠电量.exe`。四个程序放在同一目录。
3. 连接外设。蓝牙设备先在 Windows 配对。设备休眠时，唤醒后可在面板点「刷新全部」。

在任务栏图标的右键菜单里，可以设置开机自动显示或退出。降低到 20% 以下时，程序会发出低电量提醒。

## 兼容范围

[兼容清单](兼容清单.md) 按型号列出原装 2.4G 接收器、USB 数据线和蓝牙的适配情况。电量、充电标记和 DPI 取决于设备和固件回报的数据。新增型号通过了软件和报文测试，清单注明了已有的实机验证和待核实的连接方式。

测试反馈请带上 Windows 版本、外设完整型号、连接方式和面板读数。有厂商软件或系统对照读数时，也可以一并提供。[提交反馈](https://github.com/student2026ypx-ai/peripheral-battery-tray/issues/new)

## 从源码编译

在 Windows x64 上安装 Windows 10 或 Windows 11 SDK。编译脚本使用系统的 .NET Framework C# 编译器和 SDK 的 Windows.winmd。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\源码\编译.ps1
```

编译会在项目根目录生成 `键鼠电量.exe` 和三个外设监控程序。查看[版权与协议参考](版权与协议参考.md)。
