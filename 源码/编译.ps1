$ErrorActionPreference = 'Stop'
$batterySourceDirectory = $PSScriptRoot
$batteryOutputDirectory = Split-Path -Parent $batterySourceDirectory
$batteryCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$batteryStartupSource = Join-Path $batterySourceDirectory 'Startup.cs'
$batterySdkMetadata = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata\*\Windows.winmd" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -as [version] } |
    Sort-Object { [version]$_.Directory.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
if (!$batterySdkMetadata) { throw '编译需要 Windows 10 或 Windows 11 SDK 的 Windows.winmd。' }
$batteryWinRt = @('System.Runtime','System.Runtime.WindowsRuntime','System.Runtime.InteropServices.WindowsRuntime') | ForEach-Object {
    Get-ChildItem -LiteralPath (Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL\$_") -Recurse -Filter "$_.dll" |
        Select-Object -First 1 -ExpandProperty FullName
}
$batteryWinRtReferences = @($batterySdkMetadata) + $batteryWinRt | ForEach-Object { "/r:$_" }
$batterySources = @(
    'WindowsDevices.cs', 'HidTransport.cs', 'HidBattery.cs', 'BatteryReader.cs', 'LogitechInputBattery.cs', 'LogitechMouseDpi.cs', 'AsusAimPointMouse.cs',
    'UsbMouseSupport.cs', 'RazerMouse.cs', 'MchoseMouse.cs', 'DareuMouse.cs', 'AulaMouse.cs',
    'UsbKeyboardSupport.cs', 'MchoseKeyboard.cs', 'UsbHeadsetSupport.cs', 'BluetoothBatteryService.cs',
    'ExpandedUsbBattery.cs', 'ExpandedMouseBattery.cs', 'MouseReceiverBattery.cs', 'SupplementalMouseBattery.cs', 'ExpandedKeyboardBattery.cs', 'ExpandedHeadsetBattery.cs',
    'SteelSeriesHeadsetBattery.cs', 'HeadsetReceiverBattery.cs', 'HyperXHeadsetBattery.cs',
    'CorsairHeadsetBattery.cs', 'RazerHeadsetBattery.cs', 'LogitechHeadsetBattery.cs',
    'MouseDpiQueries.cs', 'VgnMouseDpi.cs', 'CompxMouseDpi.cs', 'EweadnMouseDpi.cs',
    'EweadnKeyboardBattery.cs', 'HyzenKeyboardBattery.cs', 'MchoseV3KeyboardBattery.cs',
    'KeyboardProtocol.cs', 'KeyboardBatteryEvents.cs', 'KeyboardLighting.cs', 'BluetoothInputBattery.cs',
    'BluetoothHeadsetBattery.cs', 'WindowsAudioOutputs.cs', 'WindowsSystemVolume.cs', 'WindowsQuickAudio.cs', 'PeripheralControls.cs',
    'BluetoothHeadsetProtocols.cs', 'BluetoothHeadsetFrames.cs', 'SupplementalHeadsetFrames.cs', 'BluetoothHeadsetLowEnergy.cs', 'RfcommBatteryLink.cs',
    'DeviceBatteryPanel.cs', 'QuickControlPage.cs', 'TrayApp.cs', 'TaskbarDisplay.cs', 'LowBatteryAlerts.cs', 'Startup.cs'
) | ForEach-Object { Join-Path $batterySourceDirectory $_ }
$batteryTargets = @(
    @{Name='鼠标电量.exe'; Define='MOUSE_APP'}
    @{Name='键盘电量.exe'; Define='KEYBOARD_APP'}
    @{Name='耳机电量.exe'; Define='HEADSET_APP'}
)
foreach ($batteryTarget in $batteryTargets) {
    $batteryOutputExe = Join-Path $batteryOutputDirectory $batteryTarget.Name
    & $batteryCompiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ "/define:$($batteryTarget.Define)" "/out:$batteryOutputExe" /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll @batteryWinRtReferences @batterySources
    if ($LASTEXITCODE -ne 0) { throw "生成 $($batteryTarget.Name) 失败，请检查编译信息。" }
    Write-Output "已生成：$batteryOutputExe"
}
$batteryLauncherExe = Join-Path $batteryOutputDirectory '键鼠电量.exe'
& $batteryCompiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$batteryLauncherExe" /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Windows.Forms.dll (Join-Path $batterySourceDirectory 'Launcher.cs') $batteryStartupSource
if ($LASTEXITCODE -ne 0) { throw '启动器编译失败。' }
Write-Output "已生成：$batteryLauncherExe"
