namespace PeripheralBattery {
    internal interface IPeripheralControls {
        MouseDpiState ReadMouse(string key);
        MouseDpiState SetDpi(string key, MouseDpiState expected, int stage, out bool applied);
        KeyboardLightState ReadKeyboard(string key);
        KeyboardLightState SetLight(string key, KeyboardLightState expected, bool enable, out bool applied);
    }

    internal sealed class PeripheralControls : IPeripheralControls {
        readonly string directory;
        public PeripheralControls(string directory) { this.directory = directory; }

        internal static bool SupportsMouseDpi(string name) {
            return LogitechInputBattery.SupportsDpi(name) || AsusAimPointMouse.SupportsDpi(name) ||
                UsbMouseSupport.SupportsDpi(name) || ExpandedMouseBattery.SupportsDpi(name) ||
                ExpandedUsbBattery.SupportsMouseDpi(name);
        }

        public MouseDpiState ReadMouse(string key) {
            bool ignored;
            return SetDpi(key, null, -1, out ignored);
        }

        public MouseDpiState SetDpi(string key, MouseDpiState expected, int stage, out bool applied) {
            var devices = HidNative.Enumerate();
            MouseDpiState reported;
            if (ExpandedUsbBattery.TryReadMouseDpi(devices,key,out reported)) {
                applied = false;
                return reported;
            }
            var expanded = ExpandedMouseBattery.FindDpiChannel(devices,key);
            if (expanded != null) {
                applied = false;
                return HidLink.Use(expanded,"mouse",ExpandedMouseBattery.ReadDpi);
            }
            if (UsbMouseSupport.IsReadOnlyKey(devices, key)) {
                applied = false;
                return UsbMouseSupport.ReadDpi(devices, key);
            }
            var asus = AsusAimPointMouse.FindChannel(devices, key);
            return asus == null ? LogitechInputBattery.MouseDpi(key, expected, stage, out applied, devices)
                : AsusAimPointMouse.QueryDpi(asus, expected, stage, out applied);
        }

        public KeyboardLightState ReadKeyboard(string key) {
            bool ignored;
            return KeyboardLighting.Query(key, null, null, directory, out ignored);
        }

        public KeyboardLightState SetLight(string key, KeyboardLightState expected, bool enable, out bool applied) {
            return KeyboardLighting.Query(key, expected, enable, directory, out applied);
        }
    }
}
