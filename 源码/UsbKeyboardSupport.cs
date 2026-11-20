using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PeripheralBattery {
    internal static class UsbKeyboardSupport {
        sealed class RazerModel {
            public string Name, PreferredInterface;
            public byte Transaction;
            public bool Wireless;
        }

        static readonly Dictionary<ushort, RazerModel> RazerModels = new Dictionary<ushort, RazerModel> {
            { 0x025C, Razer("BlackWidow V3 Pro", 0x9F, true) },
            { 0x025A, Razer("BlackWidow V3 Pro", 0x3F, false) },
            { 0x0271, Razer("BlackWidow V3 Mini HyperSpeed", 0x9F, true, "&mi_03") },
            { 0x0258, Razer("BlackWidow V3 Mini HyperSpeed", 0x1F, false, "&mi_03") },
            { 0x0290, Razer("DeathStalker V2 Pro", 0x9F, true, "&mi_02") },
            { 0x0292, Razer("DeathStalker V2 Pro", 0x1F, false, "&mi_03") },
            { 0x0296, Razer("DeathStalker V2 Pro TKL", 0x9F, true, "&mi_02") },
            { 0x0298, Razer("DeathStalker V2 Pro TKL", 0x1F, false, "&mi_03") },
            { 0x02BA, Razer("BlackWidow V4 Mini HyperSpeed", 0x9F, true, "&mi_03") },
            { 0x02B9, Razer("BlackWidow V4 Mini HyperSpeed", 0x1F, false, "&mi_03") },
            { 0x02D5, Razer("BlackWidow V4 Tenkeyless HyperSpeed", 0x9F, true, "&mi_02") },
            { 0x02D7, Razer("BlackWidow V4 Tenkeyless HyperSpeed", 0x1F, false, "&mi_03") },
            { 0x02D2, Razer("BlackWidow V4 Low Profile Tenkeyless HyperSpeed", 0x9F, true, "&mi_02") },
            { 0x02D4, Razer("BlackWidow V4 Low Profile Tenkeyless HyperSpeed", 0x1F, false, "&mi_03") },
            { 0x02CD, Razer("Joro", 0x1F, false, "&mi_03") },
            { 0x027B, Razer("Pro Type Ultra", 0x9F, true) }
        };

        static RazerModel Razer(string name, byte transaction, bool wireless, string preferredInterface = null) {
            return new RazerModel { Name = "Razer " + name, Transaction = transaction,
                Wireless = wireless, PreferredInterface = preferredInterface };
        }

        static bool EweadnReceiver(ushort vendor, ushort product) { return vendor == 0x0C45 && product == 0xFEFE; }

        internal static bool KnownId(ushort vendor, ushort product) {
            return (vendor == 0x1532 && RazerModels.ContainsKey(product)) ||
                MchoseKeyboard.IsProduct(vendor, product) || EweadnReceiver(vendor, product);
        }

        internal static bool IsChannel(HidInfo info) {
            if (info.Transport != "usb" || !KnownId(info.Vid, info.Pid)) return false;
            if (info.Vid == 0x1532) return info.FeatureLength == 91;
            if (MchoseKeyboard.IsProduct(info.Vid, info.Pid)) return MchoseKeyboard.IsChannel(info);
            return info.UsagePage == 0xFF60 && info.Usage == 0x61 && info.InputLength == 33 &&
                info.OutputLength == 33 && (info.Path ?? "").IndexOf("&mi_03", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static BatteryState Read(IEnumerable<HidInfo> group) {
            var channels = group.Where(IsChannel).ToList();
            if (channels.Count == 0) return null;
            var first = channels[0];
            RazerModel razer;
            if (first.Vid == 0x1532 && RazerModels.TryGetValue(first.Pid, out razer)) {
                var state = BatteryReader.UsbState(first, "keyboard", razer.Name,
                    razer.Wireless ? "wireless" : "usb", "键盘暂未返回电量");
                foreach (var channel in channels.OrderByDescending(info => razer.PreferredInterface != null &&
                    (info.Path ?? "").IndexOf(razer.PreferredInterface, StringComparison.OrdinalIgnoreCase) >= 0)) {
                    var power = HidLink.Use(channel, "keyboard", link => ReadRazer(link, razer.Transaction));
                    if (power == null) continue;
                    state.Percent = power.Percent;
                    state.Charging = power.Charging;
                    state.Detail = "雷蛇键盘电量";
                    break;
                }
                return state;
            }
            if (MchoseKeyboard.IsProduct(first.Vid, first.Pid)) {
                foreach (var channel in channels) {
                    var reading = HidLink.Use(channel, "keyboard", MchoseKeyboard.ReadPower);
                    if (reading != null) return reading;
                }
                return BatteryReader.UsbState(first, "keyboard",
                    MchoseKeyboard.IsWired(first) ? first.Product ?? "MCHOSE USB 键盘" : "MCHOSE 键盘接收器",
                    MchoseKeyboard.IsWired(first) ? "usb" : "wireless", "键盘暂未响应");
            }

            // The VID/PID is shared by Sonix-based keyboards. Use the USB name if available.
            var label = group.Select(info => info.Product).FirstOrDefault(name => !String.IsNullOrWhiteSpace(name));
            if (String.Equals(label, "X87", StringComparison.OrdinalIgnoreCase)) label = "前行者 X87";
            var eweadn = BatteryReader.UsbState(first, "keyboard", label ?? "2.4G 键盘接收器",
                "wireless", "键盘暂未返回电量");
            var reply = HidLink.Use(first, "keyboard", ReadEweadn);
            if (reply != null) {
                eweadn.Percent = ParseEweadn(reply);
                eweadn.Detail = "键盘接收器报告的电量";
            }
            return eweadn;
        }

        static byte[] QueryRazer(HidLink link, byte command, byte transaction) {
            if (!link.WriteFeature(RazerMouse.BuildRequest(7, command, 2, transaction: transaction))) return null;
            for (int attempt = 0; attempt < 5; attempt++) {
                Thread.Sleep(attempt == 0 ? 55 : 40);
                var reply = link.ReadFeature(0);
                if (RazerMouse.Matches(reply, 7, command, 2, transaction)) return reply;
                if (reply == null || (reply[2] == transaction && reply[7] == 7 && reply[8] == command &&
                    (reply[1] == 4 || reply[1] == 5))) break;
            }
            return null;
        }

        static UsbMousePower ReadRazer(HidLink link, byte transaction) {
            var level = QueryRazer(link, 0x80, transaction);
            return level == null ? null : RazerMouse.ParsePower(level, QueryRazer(link, 0x84, transaction), transaction);
        }

        internal static byte[] EweadnRequest() {
            var frame = new byte[33];
            frame[1] = 0x20;
            frame[2] = 0x01;
            frame[32] = 0x21;
            return frame;
        }

        internal static int? ParseEweadn(byte[] frame) {
            if (frame == null || frame.Length != 33 || frame[0] != 0 || frame[1] != 0x20 ||
                frame[2] != 1 || frame[3] != 0 || frame[4] == 0 || frame[4] > 100) return null;
            int check = frame.Take(32).Sum(value => (int)value) & 255;
            return frame[32] == check || frame[32] == 0 ? (int?)frame[4] : null;
        }

        static byte[] ReadEweadn(HidLink link) {
            if (!HidNative.HasReport(link.Handle, 1, 0, 33) || !HidNative.HasReport(link.Handle, 0, 0, 33)) return null;
            link.Write(EweadnRequest());
            return link.ReadReply(reply => ParseEweadn(reply).HasValue, 1200);
        }
    }
}
