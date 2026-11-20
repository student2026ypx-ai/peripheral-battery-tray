using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    internal static class AsusAimPointMouse {
        internal const string Name = "ROG Gladius III Wireless AimPoint";
        const ushort Vendor = 0x0B05, Wired = 0x1A70, Receiver = 0x1A72;
        const int ReportSize = 65, StageSlots = 4;
        const string QueryGate = @"Local\PeripheralBatteryTray.AsusAimPoint";

        // ASUS protocol described by libratbag and ROGDRV Web. These requests use Windows' report ID 0.
        static readonly byte[] PowerRequest = { 0, 0x12, 0x07 };
        static readonly byte[] ProfileRequest = { 0, 0x12, 0x00 };
        static readonly byte[] DpiRequest = { 0, 0x12, 0x04, 0x02 };
        static readonly byte[] SingleAxisDpiRequest = { 0, 0x12, 0x04, 0x00 };

        internal static bool IsProduct(ushort vid, ushort pid) {
            return vid == Vendor && (pid == Wired || pid == Receiver);
        }

        internal static string Connection(HidInfo info) { return info.Pid == Receiver ? "wireless" : "usb"; }

        internal static bool SupportsDpi(string name) {
            return String.Equals(Name, name, StringComparison.OrdinalIgnoreCase) ||
                String.Equals("ROG GIII WIRELESS AIMPOINT", name, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsChannel(HidInfo info) {
            return IsProduct(info.Vid, info.Pid) && info.Transport == "usb" && info.UsagePage == 0xFF01 &&
                info.Usage == 1 && info.InputLength == ReportSize && info.OutputLength == ReportSize &&
                info.Path.IndexOf("&mi_00", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static HidInfo FindChannel(IEnumerable<HidInfo> devices, string key) {
            return devices.FirstOrDefault(info => IsChannel(info) && BatteryReader.UsbDeviceKey(info, "mouse") == key);
        }

        internal static void ReadPower(HidInfo channel, BatteryState reading) {
            var report = UseChannel(channel, link => Exchange(link, PowerRequest));
            int percent;
            bool charging;
            if (!TryPower(report, out percent, out charging)) return;
            reading.Percent = percent;
            reading.Charging = charging;
            reading.CheckedAt = DateTime.Now;
            reading.Detail = "ROG 鼠标电量与充电状态";
        }

        internal static bool TryPower(byte[] report, out int percent, out bool charging) {
            percent = 0;
            charging = false;
            if (!Matches(report, PowerRequest) || report[5] > 100 || (report[5] == 0 && report[10] == 0)) return false;
            percent = report[5];
            charging = report[10] != 0;
            return true;
        }

        internal static MouseDpiState QueryDpi(HidInfo channel, MouseDpiState expected, int stage, out bool applied) {
            bool confirmed = false;
            var current = UseChannel(channel, link => {
                var state = ReadDpi(link);
                if (stage < 0 || state == null || !state.SameProfile(expected) ||
                        stage >= expected.VisibleStageCount || state.Presets[stage] == 0) return state;
                if (state.Index == stage) {
                    confirmed = true;
                    return state;
                }

                // Select the existing stage on click. The stored values and profile are left intact.
                Exchange(link, new byte[] { 0, 0x51, 0x31, 0x09, 0, (byte)(stage + 1) });
                var updated = ReadDpi(link);
                confirmed = updated != null && updated.Index == stage && updated.SameProfile(state);
                return updated;
            });
            applied = confirmed;
            return current;
        }

        internal static MouseDpiState ReadDpi(HidLink link, bool separateAxes = true,
                int minimumDpi = 100, int maximumDpi = 36000) {
            var before = Exchange(link, ProfileRequest);
            if (before == null) return null;
            var settings = Exchange(link, separateAxes ? DpiRequest : SingleAxisDpiRequest);
            if (settings == null) return null;
            var after = Exchange(link, ProfileRequest);
            return after != null && after[11] == before[11]
                ? ParseDpi(after, settings, separateAxes, minimumDpi, maximumDpi) : null;
        }

        internal static MouseDpiState ParseDpi(byte[] profile, byte[] settings, bool separateAxes = true,
                int minimumDpi = 100, int maximumDpi = 36000) {
            if (!Matches(profile, ProfileRequest) ||
                !Matches(settings, separateAxes ? DpiRequest : SingleAxisDpiRequest)) return null;
            int index = profile[12] - 1;
            if (index < 0 || index >= StageSlots || profile[11] > 4) return null;
            var horizontal = new int[StageSlots];
            var vertical = new int[StageSlots];
            for (int stage = 0; stage < StageSlots; stage++) {
                int offset = 5 + stage * (separateAxes ? 4 : 2);
                int x = DecodeDpi(settings, offset, minimumDpi, maximumDpi);
                int y = separateAxes ? DecodeDpi(settings, offset + 2, minimumDpi, maximumDpi) : x;
                if (x == 0 || y == 0) continue;
                horizontal[stage] = x;
                vertical[stage] = y;
            }
            if (horizontal[index] == 0) return null;
            // The reply contains all stored slots. The active stage confirms which slots are in use
            // without advancing the mouse through its settings in the background.
            return new MouseDpiState {
                Mode = 1, Profile = profile[11], Index = index, Current = horizontal[index],
                Presets = horizontal, VerticalPresets = vertical, ConfirmedStages = Math.Max(2, index + 1)
            };
        }

        static int DecodeDpi(byte[] settings, int offset, int minimumDpi, int maximumDpi) {
            int dpi = ((settings[offset] | settings[offset + 1] << 8) + 1) * 50;
            return dpi >= minimumDpi && dpi <= maximumDpi ? dpi : 0;
        }

        static bool Matches(byte[] report, byte[] request) {
            return report != null && report.Length == ReportSize && report[0] == 0 &&
                report[1] == request[1] && report[2] == request[2] &&
                (request[2] != 0x04 || report[3] == request[3]);
        }

        static byte[] Exchange(HidLink link, byte[] request) {
            link.Write(request);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 450) {
                var report = link.Read(75);
                if (Matches(report, request)) return report;
                if (report != null && report.Length >= 3 && report[0] == 0 &&
                        (report[1] == 0xFF && report[2] == 0xAA || report[1] == 0xAA && report[2] == 0xFF)) return null;
            }
            return null;
        }

        static T UseChannel<T>(HidInfo channel, Func<HidLink, T> action) where T : class {
            try {
                using (var gate = new Mutex(false, QueryGate)) {
                    bool entered;
                    try { entered = gate.WaitOne(850); }
                    catch (AbandonedMutexException) { entered = true; }
                    if (!entered) return null;
                    try {
                        using (var link = new HidLink(channel)) return action(link);
                    } finally { gate.ReleaseMutex(); }
                }
            } catch (Win32Exception) {} catch (TimeoutException) {} catch (UnauthorizedAccessException) {}
            return null;
        }
    }
}
