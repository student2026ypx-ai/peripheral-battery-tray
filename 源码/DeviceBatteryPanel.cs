using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PeripheralBattery {
    enum BatteryBand { Unknown, Red, Yellow, Green }

    static class BatteryColors {
        const int LowThreshold = 20;
        const int GreenThreshold = 50;

        public static BatteryBand Band(int? percent, bool stale) {
            if (stale || !percent.HasValue || percent.Value < 0 || percent.Value > 100) return BatteryBand.Unknown;
            if (percent.Value < LowThreshold) return BatteryBand.Red;
            if (percent.Value < GreenThreshold) return BatteryBand.Yellow;
            return BatteryBand.Green;
        }
    }

    static class BatteryRefresh {
        public static string EventName(string kind) { return @"Local\PeripheralBatteryTray.Refresh." + kind; }

        public static bool Request(string kind) { return Visit(kind, true); }
        public static bool IsRunning(string kind) { return Visit(kind, false); }

        static bool Visit(string kind, bool signal) {
            try {
                EventWaitHandle handle;
                if (!EventWaitHandle.TryOpenExisting(EventName(kind), out handle)) return false;
                using (handle) { if (signal) handle.Set(); }
                return true;
            } catch (UnauthorizedAccessException) { return false; }
        }
    }

    sealed class PanelReading {
        public string Name, Detail, DeviceKey;
        public int? Percent;
        public bool Stale, Charging;
        public BatteryBand Band { get { return BatteryColors.Band(Percent, Stale); } }
    }

    sealed class PanelReadings {
        public static readonly string[] Kinds = { "mouse", "keyboard", "headset" };
        public static readonly string[] Labels = { "鼠标", "键盘", "耳机" };
        readonly CachedDevice[] lastGood = new CachedDevice[Kinds.Length];
        readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 20 };
        readonly string directory;
        readonly Func<string, bool> isRunning;

        public PanelReadings(string directory, Func<string, bool> isRunning) { this.directory = directory; this.isRunning = isRunning; }

        public PanelReading Read(int index) {
            string kind = Kinds[index], label = Labels[index];
            CachedDevice next = ReadSnapshot(Path.Combine(directory, label + "电量状态.json"), kind);
            // A partially written file is retried on the next tick; never replace a valid reading with it.
            if (next != null) lastGood[index] = next;
            return Present(lastGood[index], label, isRunning(kind));
        }

        CachedDevice ReadSnapshot(string file, string kind) {
            try {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) {
                    if (stream.Length > serializer.MaxJsonLength) return null;
                    var snapshot = serializer.Deserialize<ReadingCache>(reader.ReadToEnd());
                    if (snapshot != null && snapshot.devices != null)
                        foreach (var entry in snapshot.devices)
                            if (entry != null && entry.kind == kind && !String.IsNullOrWhiteSpace(entry.name)) return entry;
                }
            } catch (IOException) {} catch (UnauthorizedAccessException) {} catch (ArgumentException) {} catch (InvalidOperationException) {}
            return null;
        }

        static int? Valid(int? value) { return value.HasValue && value.Value >= 0 && value.Value <= 100 ? value : null; }

        internal static PanelReading Present(CachedDevice device, string label, bool running) {
            var result = new PanelReading { Name = device == null ? label : device.name };
            if (device == null) {
                result.Detail = running ? "正在读取…" : "监测未运行";
                return result;
            }
            if (running && !String.IsNullOrWhiteSpace(device.deviceKey)) {
                result.DeviceKey = device.deviceKey;
            }
            DateTime when;
            string time = DateTime.TryParse(device.lastSeen, out when) ? when.ToString("HH:mm") : null;
            int? current = Valid(device.percent);
            result.Percent = running && !device.stale ? current : null;
            result.Charging = running && !device.stale && device.charging == true;
            if (result.Percent.HasValue || result.Charging) {
                result.Detail = result.Charging ? "充电中" : "已连接";
                if (result.Percent.HasValue && device.estimated) result.Detail += " · 估算电量";
                if (time != null && result.Percent.HasValue) result.Detail += " · " + time;
                return result;
            }
            result.Percent = Valid(device.lastPercent) ?? (!running ? current : null);
            result.Stale = result.Percent.HasValue;
            if (!running) result.Detail = "监测已停止";
            else if (device.detail == "耳机未连接" || (device.detail ?? "").StartsWith("未检测到外接", StringComparison.Ordinal)) result.Detail = "未连接";
            else if (result.Stale) result.Detail = "暂未读到电量";
            else result.Detail = String.IsNullOrWhiteSpace(device.detail) ? "暂未读到电量" : device.detail;
            if (result.Stale) result.Detail += time == null ? " · 上次读数" : " · 上次 " + time;
            return result;
        }
    }

    sealed class DeviceBatteryPanel : Form {
        const string PanelTitle = "外设面板";
        const int PageTop = 58, BatteryTop = 91, BatteryRowHeight = 74, PanelHeight = 338;
        const string PanelMarker = "PeripheralBatteryTray.UnifiedDevicePanel.1";
        const string PanelGate = @"Local\PeripheralBatteryTray.UnifiedDevicePanel.Open";
        static readonly Color Surface = Color.FromArgb(249, 251, 253);
        internal static readonly Color Ink = Color.FromArgb(29, 42, 57);
        static readonly Color Muted = Color.FromArgb(91, 105, 124);
        static readonly Color Edge = Color.FromArgb(218, 226, 234);
        static readonly Color Rule = Color.FromArgb(226, 233, 240);
        static readonly Color Track = Color.FromArgb(226, 232, 239);
        static readonly Color Power = Color.FromArgb(8, 124, 100);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr window, string text, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr window, string text);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

        readonly PanelReadings source;
        readonly float scale;
        readonly Label[] names = new Label[PanelReadings.Kinds.Length];
        readonly Label[] details = new Label[PanelReadings.Kinds.Length];
        readonly Label[] values = new Label[PanelReadings.Kinds.Length];
        readonly PanelReading[] readings = new PanelReading[PanelReadings.Kinds.Length];
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 700 };
        readonly ToolTip hints = new ToolTip { InitialDelay = 450 };
        readonly Font titleFont, buttonFont, nameFont, detailFont, valueFont;
        readonly Button batteryTab, controlsTab, refresh;
        readonly Panel batteryPage;
        readonly QuickControlPage quickPage;
        readonly Func<string, bool> requestRefresh;
        DateTime nextMouseRefresh;
        bool showingQuickControls;
        bool closing;
        IntPtr markedHandle;

        static IntPtr FindOpenPanel() {
            IntPtr window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, null, PanelTitle)) != IntPtr.Zero)
                if (GetProp(window, PanelMarker) != IntPtr.Zero && IsWindowVisible(window)) return window;
            return IntPtr.Zero;
        }

        public static DeviceBatteryPanel ShowShared(Rectangle anchor, float dpiScale) {
            using (var gate = new Mutex(false, PanelGate)) {
                try { if (!gate.WaitOne(250)) return null; }
                catch (AbandonedMutexException) { /* The abandoned mutex is now acquired. */ }
                try {
                    IntPtr existing = FindOpenPanel();
                    if (existing != IntPtr.Zero) {
                        SetForegroundWindow(existing);
                        return null;
                    }
                    var panel = new DeviceBatteryPanel(anchor, Screen.FromRectangle(anchor).WorkingArea, dpiScale, AppDomain.CurrentDomain.BaseDirectory, BatteryRefresh.IsRunning, BatteryRefresh.Request);
                    panel.Show();
                    panel.Activate();
                    return panel;
                } finally { gate.ReleaseMutex(); }
            }
        }

        internal DeviceBatteryPanel(Rectangle anchor, Rectangle work, float dpiScale, string directory, Func<string, bool> isRunning, Func<string, bool> requestRefresh,
            ISystemVolume systemVolume = null, IAudioControls audio = null, IPeripheralControls peripherals = null) {
            source = new PanelReadings(directory, isRunning);
            this.requestRefresh = requestRefresh;
            scale = Math.Min(dpiScale, Math.Min(work.Width / 434f, work.Height / (PanelHeight + 20f)));
            Text = PanelTitle; AccessibleName = PanelTitle;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            BackColor = Surface; DoubleBuffered = true;
            ClientSize = new Size(Px(414), Px(PanelHeight));
            Location = PlaceNextTo(anchor, work, Size, Px(10));
            using (var outline = TrayDrawing.RoundedRect(0, 0, Width, Height, Px(17))) Region = new Region(outline);

            titleFont = new Font("Microsoft YaHei UI", Px(15), FontStyle.Bold, GraphicsUnit.Pixel);
            buttonFont = new Font("Microsoft YaHei UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            nameFont = new Font("Microsoft YaHei UI", Px(15), FontStyle.Bold, GraphicsUnit.Pixel);
            detailFont = new Font("Microsoft YaHei UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            valueFont = new Font("Segoe UI", Px(30), FontStyle.Bold, GraphicsUnit.Pixel);
            batteryTab = MakeTab("batteryTab", "设备电量", 20, 0, false);
            controlsTab = MakeTab("controlsTab", "快捷控制", 121, 1, true);
            refresh = new Button {
                Name = "refreshAll", Text = "刷新全部", AccessibleName = "刷新全部", TabIndex = 0,
                Bounds = Box(287, 0, 87, 29), Font = buttonFont, Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, BackColor = Color.FromArgb(238, 244, 249), ForeColor = Ink
            };
            refresh.FlatAppearance.BorderSize = 0;
            refresh.FlatAppearance.MouseOverBackColor = Color.FromArgb(225, 236, 246);
            refresh.FlatAppearance.MouseDownBackColor = Color.FromArgb(216, 229, 240);
            using (var outline = TrayDrawing.RoundedRect(0, 0, refresh.Width, refresh.Height, Px(8))) refresh.Region = new Region(outline);
            refresh.Click += delegate { foreach (string kind in PanelReadings.Kinds) requestRefresh(kind); };
            batteryPage = new Panel { Name = "batteryPage", AccessibleName = "设备电量", Bounds = Box(20, PageTop, 374, 271),
                BackColor = Surface, TabIndex = 3, TabStop = false };
            Controls.Add(batteryPage);
            batteryPage.Controls.Add(refresh);
            var readingTitle = AddLabel(batteryPage, "batterySummary", "设备名称及电量", 0, 0, 200, 29, detailFont, Muted, ContentAlignment.MiddleLeft, false);
            readingTitle.TabStop = false;

            for (int i = 0; i < readings.Length; i++) {
                string kind = PanelReadings.Kinds[i];
                int y = BatteryTop - PageTop + BatteryRowHeight * i;
                names[i] = AddLabel(batteryPage, kind + "Name", "", 58, y + 10, 198, 23, nameFont, Ink, ContentAlignment.MiddleLeft, true);
                details[i] = AddLabel(batteryPage, kind + "Status", "", 58, y + 33, 198, 19, detailFont, Muted, ContentAlignment.MiddleLeft, true);
                values[i] = AddLabel(batteryPage, kind + "Percent", "", 258, y + 7, 116, 47, valueFont, Muted, ContentAlignment.MiddleRight, false);
            }
            batteryPage.Paint += PaintBatteryList;
            quickPage = new QuickControlPage(scale, Surface, systemVolume ?? new WindowsAudioOutputs.SystemVolume(),
                audio ?? new WindowsAudioOutputs.QuickAudio(), peripherals ?? new PeripheralControls(directory)) {
                Location = new Point(Px(20), Px(PageTop)), TabIndex = 3
            };
            Controls.Add(quickPage);
            SelectPage(false);
            LoadLatest();
            timer.Tick += delegate { LoadLatest(); };
            timer.Start();
        }

        static Point PlaceNextTo(Rectangle anchor, Rectangle work, Size size, int gap) {
            int x = Math.Max(work.Left + gap, Math.Min(work.Right - size.Width - gap, anchor.Right - size.Width));
            int y = anchor.Top + anchor.Height / 2 < work.Top
                ? anchor.Bottom + gap : Math.Min(work.Bottom, anchor.Top) - gap - size.Height;
            return new Point(x, Math.Max(work.Top + gap, Math.Min(work.Bottom - size.Height - gap, y)));
        }

        int Px(float value) { return Math.Max(1, (int)Math.Round(value * scale)); }
        Rectangle Box(int x, int y, int width, int height) { return new Rectangle((int)Math.Round(x * scale), (int)Math.Round(y * scale), Px(width), Px(height)); }

        Label AddLabel(Control parent, string id, string text, int x, int y, int width, int height, Font font, Color color, ContentAlignment align, bool ellipsis) {
            var label = new Label { Name = id, Text = text, AutoSize = false, Bounds = Box(x,y,width,height),
                Font = font, ForeColor = color, TextAlign = align, BackColor = Surface, AutoEllipsis = ellipsis, UseMnemonic = false };
            parent.Controls.Add(label);
            return label;
        }

        Button MakeTab(string id, string text, int x, int index, bool controls) {
            var button = new Button { Name = id, Text = text, AccessibleName = text, TabIndex = index,
                Bounds = Box(x, 12, 93, 34), Font = titleFont, Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(233, 241, 249);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(215, 230, 248);
            using (var outline = TrayDrawing.RoundedRect(0, 0, button.Width, button.Height, Px(8))) button.Region = new Region(outline);
            button.Click += delegate { SelectPage(controls); };
            Controls.Add(button);
            return button;
        }

        void SelectPage(bool controls) {
            showingQuickControls = controls;
            batteryTab.BackColor = controls ? Surface : Color.FromArgb(232, 241, 253);
            controlsTab.BackColor = controls ? Color.FromArgb(232, 241, 253) : Surface;
            batteryTab.ForeColor = controls ? Muted : Color.FromArgb(27, 82, 159);
            controlsTab.ForeColor = controls ? Color.FromArgb(27, 82, 159) : Muted;
            batteryTab.AccessibleDescription = controls ? "未选中" : "已选中";
            controlsTab.AccessibleDescription = controls ? "已选中" : "未选中";
            batteryPage.Visible = !controls;
            quickPage.Visible = controls;
            if (controls) {
                quickPage.SetPeripherals(readings[0], readings[1]);
                quickPage.StartControls();
            } else {
                quickPage.StopControls();
            }
            Invalidate();
        }

        static Color NumberColor(BatteryBand band) {
            switch (band) {
                case BatteryBand.Green: return Power;
                case BatteryBand.Yellow: return Color.FromArgb(138, 93, 0);
                case BatteryBand.Red: return Color.FromArgb(189, 44, 57);
                default: return Muted;
            }
        }
        static Color RingColor(BatteryBand band) {
            switch (band) {
                case BatteryBand.Green: return Color.FromArgb(13, 150, 111);
                case BatteryBand.Yellow: return Color.FromArgb(204, 148, 0);
                case BatteryBand.Red: return Color.FromArgb(209, 58, 72);
                default: return Color.FromArgb(136, 149, 166);
            }
        }

        internal void LoadLatest() {
            for (int i = 0; i < readings.Length; i++) {
                PanelReading row = source.Read(i), previous = readings[i];
                readings[i] = row;
                if (previous != null && previous.Name == row.Name && previous.Detail == row.Detail && previous.Percent == row.Percent && previous.Band == row.Band && previous.Charging == row.Charging) continue;
                names[i].Text = row.Name;
                names[i].AccessibleName = PanelReadings.Labels[i] + " · " + row.Name;
                details[i].Text = row.Detail;
                values[i].Text = row.Percent.HasValue ? row.Percent.Value + "%" : "--";
                values[i].AccessibleName = PanelReadings.Labels[i] + "电量 " + (row.Percent.HasValue ? values[i].Text : "未知");
                values[i].ForeColor = NumberColor(row.Band);
                hints.SetToolTip(names[i], row.Name);
                hints.SetToolTip(details[i], row.Detail);
                batteryPage.Invalidate(Box(0, BatteryTop - PageTop + 3 + BatteryRowHeight * i, 50, 57));
            }
            if (showingQuickControls) {
                quickPage.SetPeripherals(readings[0], readings[1]);
                if (readings[0].Name == "罗技 LIGHTSPEED 接收器" && DateTime.UtcNow >= nextMouseRefresh) {
                    nextMouseRefresh = DateTime.UtcNow.AddSeconds(8);
                    requestRefresh("mouse");
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(scale, scale);
            using (var pen = new Pen(Edge, 1.4f))
            using (var path = TrayDrawing.RoundedRect(0.8f, 0.8f, ClientSize.Width / scale - 1.6f, ClientSize.Height / scale - 1.6f, 16)) g.DrawPath(pen, path);
            using (var rule = new Pen(Rule, 1)) g.DrawLine(rule, 20, 52, 394, 52);
        }

        void PaintBatteryList(object sender, PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(scale, scale);
            using (var rule = new Pen(Rule, 1)) {
                for (int i = 0; i < readings.Length; i++) g.DrawLine(rule, 0, BatteryTop - PageTop + BatteryRowHeight * i, 374, BatteryTop - PageTop + BatteryRowHeight * i);
            }
            g.TranslateTransform(-20, 0);
            for (int i = 0; i < readings.Length; i++) {
                if (readings[i] != null) PaintRing(g, PanelReadings.Kinds[i], readings[i], BatteryTop - PageTop + 1 + BatteryRowHeight * i);
            }
        }

        static void PaintRing(Graphics g, string kind, PanelReading reading, int y) {
            var circle = new RectangleF(22, y + 10.5f, 40, 40);
            using (var track = new Pen(Track, 3.8f)) g.DrawEllipse(track, circle);
            int level = reading.Percent.GetValueOrDefault();
            if (level > 0) {
                using (var fill = new Pen(RingColor(reading.Band), 3.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                    if (level == 100) g.DrawEllipse(fill, circle);
                    else g.DrawArc(fill, circle, -90, level * 3.6f);
                }
            }
            var saved = g.Save();
            g.TranslateTransform(26, y + 21.3f);
            TrayDrawing.DrawDeviceGlyph(g, kind, reading.Band == BatteryBand.Unknown ? Muted : Ink);
            g.Restore(saved);
            if (reading.Charging) {
                using (var white = new SolidBrush(Surface)) g.FillEllipse(white, 51, y + 7, 15, 15);
                using (var power = new SolidBrush(Power)) {
                    g.FillEllipse(power, 52, y + 8, 13, 13);
                    g.FillPolygon(Brushes.White, new[] { new PointF(59.7f,y+9.8f),new PointF(55.8f,y+15.1f),new PointF(58.0f,y+15.1f),
                        new PointF(57.4f,y+19.0f),new PointF(61.3f,y+13.8f),new PointF(59.1f,y+13.8f) });
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            markedHandle = Handle;
            SetProp(markedHandle, PanelMarker, new IntPtr(1));
        }
        protected override void OnHandleDestroyed(EventArgs e) {
            if (markedHandle != IntPtr.Zero) RemoveProp(markedHandle, PanelMarker);
            markedHandle = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }
        protected override void OnDeactivate(EventArgs e) {
            base.OnDeactivate(e);
            if (!closing && Visible) Close();
        }
        protected override void OnFormClosing(FormClosingEventArgs e) {
            closing = true;
            base.OnFormClosing(e);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab) ||
                ((batteryTab.Focused || controlsTab.Focused) && (keyData == Keys.Left || keyData == Keys.Right))) {
                SelectPage(!showingQuickControls);
                (showingQuickControls ? controlsTab : batteryTab).Focus();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void Dispose(bool disposing) {
            if (disposing && !IsDisposed) {
                closing = true;
                timer.Dispose(); hints.Dispose();
                base.Dispose(disposing);
                titleFont.Dispose(); buttonFont.Dispose(); nameFont.Dispose(); detailFont.Dispose(); valueFont.Dispose();
                return;
            }
            base.Dispose(disposing);
        }
    }
}
