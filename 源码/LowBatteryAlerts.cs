using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PeripheralBattery {
    sealed class LowBatteryAlerts {
        // USB scans may reuse a sample for two minutes while the device is still present.
        static readonly TimeSpan MaxSampleAge = TimeSpan.FromMinutes(3);
        readonly string path;
        readonly HashSet<string> alerted = new HashSet<string>(StringComparer.Ordinal);

        public LowBatteryAlerts(string path) {
            this.path = path;
            try {
                if (File.Exists(path) && new FileInfo(path).Length <= 65536)
                    alerted.UnionWith(File.ReadAllLines(path));
            } catch (IOException) {} catch (UnauthorizedAccessException) {}
        }

        public void Observe(BatteryState reading, Func<BatteryState, bool> show) {
            if (String.IsNullOrWhiteSpace(reading.DeviceKey) || DateTime.Now - reading.CheckedAt > MaxSampleAge) return;
            BatteryBand band = BatteryColors.Band(reading.Percent, false);
            if (band == BatteryBand.Unknown) return;

            string key = reading.DeviceKey;
            if (band == BatteryBand.Red) {
                if (!alerted.Contains(key) && show(reading) && alerted.Add(key)) Save();
            } else if (alerted.Remove(key)) {
                Save();
            }
        }

        void Save() {
            string pending = path + ".tmp";
            try {
                File.WriteAllLines(pending, alerted);
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
            } catch (IOException) {} catch (UnauthorizedAccessException) {}
        }
    }

    sealed class LowBatteryPopup : Form {
        const string PopupGate = @"Local\PeripheralBatteryTray.LowBatteryPopup";
        const int AcceptsNotifications = 5;
        const int NoActivate = 0x08000000, ToolWindow = 0x00000080;
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer { Interval = 6000 };
        readonly string kind, title, device, percent;
        readonly Color ink, muted, edge, accent, badge;
        readonly Font titleFont, detailFont, percentFont;
        readonly float scale;
        Mutex gate;

        LowBatteryPopup(BatteryState reading, string label) {
            kind = reading.Kind;
            title = label + "电量低";
            device = String.IsNullOrWhiteSpace(reading.Name) ? label : reading.Name;
            percent = reading.Percent.Value + "%";

            bool light = TrayDrawing.LightTheme;
            BackColor = light ? Color.FromArgb(249, 251, 253) : Color.FromArgb(32, 39, 49);
            ink = light ? Color.FromArgb(29, 42, 57) : Color.FromArgb(237, 243, 249);
            muted = light ? Color.FromArgb(91, 105, 124) : Color.FromArgb(183, 195, 209);
            edge = light ? Color.FromArgb(218, 226, 234) : Color.FromArgb(70, 82, 98);
            accent = light ? Color.FromArgb(189, 44, 57) : Color.FromArgb(255, 126, 135);
            badge = light ? Color.FromArgb(251, 233, 235) : Color.FromArgb(77, 48, 58);

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            float dpi = SystemInformation.SmallIconSize.Width / 16f;
            scale = Math.Min(dpi, Math.Min(work.Width / 364f, work.Height / 106f));
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            ClientSize = new Size(Px(336), Px(80));
            Location = new Point(work.Right - Width - Px(14), work.Bottom - Height - Px(12));
            using (var outline = TrayDrawing.RoundedRect(0, 0, Width, Height, Px(12))) Region = new Region(outline);
            Text = AccessibleName = title;
            AccessibleDescription = device + "，还剩 " + percent + "，请充电";
            titleFont = new Font("Microsoft YaHei UI", Px(15), FontStyle.Bold, GraphicsUnit.Pixel);
            detailFont = new Font("Microsoft YaHei UI", Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
            percentFont = new Font("Segoe UI", Px(24), FontStyle.Bold, GraphicsUnit.Pixel);
            hideTimer.Tick += delegate { Close(); };
        }

        public static LowBatteryPopup TryShow(BatteryState reading, string label) {
            int state;
            if (SHQueryUserNotificationState(out state) < 0 || state != AcceptsNotifications) return null;
            var sharedGate = new Mutex(false, PopupGate);
            bool entered = false;
            LowBatteryPopup popup = null;
            try {
                try { entered = sharedGate.WaitOne(0); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered) return null;
                popup = new LowBatteryPopup(reading, label);
                popup.Show();
                popup.hideTimer.Start();
                popup.gate = sharedGate;
                sharedGate = null;
                return popup;
            } catch (Win32Exception) {
                if (popup != null) popup.Dispose();
                return null;
            } finally {
                if (sharedGate != null) {
                    if (entered) sharedGate.ReleaseMutex();
                    sharedGate.Dispose();
                }
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {
                var parameters = base.CreateParams;
                parameters.ExStyle |= NoActivate | ToolWindow;
                return parameters;
            }
        }

        int Px(float value) { return (int)Math.Round(value * scale); }
        Rectangle Box(int x, int y, int width, int height) {
            return new Rectangle(Px(x), Px(y), Px(width), Px(height));
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(edge, Math.Max(1, scale)))
            using (var outline = TrayDrawing.RoundedRect(0.7f, 0.7f, Width - 1.5f, Height - 1.5f, Px(12))) g.DrawPath(pen, outline);
            using (var fill = new SolidBrush(badge)) g.FillEllipse(fill, Box(15, 19, 42, 42));
            var transform = g.Save();
            g.TranslateTransform(Px(20), Px(31));
            g.ScaleTransform(scale, scale);
            TrayDrawing.DrawDeviceGlyph(g, kind, accent);
            g.Restore(transform);

            const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g, title, titleFont, Box(70, 15, 170, 25), ink, line);
            TextRenderer.DrawText(g, device + " · 请充电", detailFont, Box(70, 41, 176, 23), muted, line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, percent, percentFont, Box(246, 17, 72, 46), accent, line | TextFormatFlags.Right);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                hideTimer.Dispose();
                titleFont.Dispose();
                detailFont.Dispose();
                percentFont.Dispose();
            }
            base.Dispose(disposing);
            if (disposing && gate != null) {
                gate.ReleaseMutex();
                gate.Dispose();
                gate = null;
            }
        }
    }
}
