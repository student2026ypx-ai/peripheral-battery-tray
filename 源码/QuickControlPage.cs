using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace PeripheralBattery {
    sealed class QuickControlPage : Panel {
        const int Gap = 8, ViewWidth = 374, ViewHeight = 270;
        static readonly Color Neutral = Color.FromArgb(240, 244, 249);
        static readonly Color Hover = Color.FromArgb(228, 238, 249);
        static readonly Color Border = Color.FromArgb(213, 223, 233);
        static readonly Color Blue = Color.FromArgb(36, 95, 183);
        static readonly Color Selected = Color.FromArgb(232, 241, 253);
        static readonly Color SelectedBorder = Color.FromArgb(146, 180, 230);
        static readonly Color SelectedText = Color.FromArgb(27, 82, 159);
        static readonly Color Muted = Color.FromArgb(82, 100, 123);
        static readonly Color Rule = Color.FromArgb(225, 232, 239);

        readonly float scale;
        readonly Font headingFont, labelFont, numberFont, smallFont;
        readonly Panel dpiChoices;
        readonly Label dpiTitle, dpiStatus, volumeValue, volumeStatus;
        readonly SystemVolumeSlider volume;
        readonly VolumeSpeaker volumeSpeaker;
        readonly ISystemVolume systemVolume;
        readonly IAudioControls audio;
        readonly IPeripheralControls peripherals;
        readonly Timer volumeMonitor = new Timer { Interval = 200 };
        readonly Timer peripheralMonitor = new Timer { Interval = 4000 };
        readonly ToolTip controlHints = new ToolTip { InitialDelay = 250, ShowAlways = true };
        OutputVolumeState volumeState;
        AudioControlsState audioState;
        PlaybackDevice speakerTarget, headsetTarget;
        string previousSpeaker, previousHeadset, mouseKey, keyboardKey;
        string dpiMessage, lightMessage;
        MouseDpiState dpiState;
        KeyboardLightState lightState;
        bool controlsOpen, mousePending, keyboardPending, mouseWriting, keyboardWriting, hasMouse, hasKeyboard, receiverOnly;
        int queuedStage = -1;
        bool? queuedLight;
        string displayedDpiStatus;
        int audioTick;
        bool applyingVolume;
        readonly AudioButton speakers, headset, microphone;
        readonly LightingRow lights;
        readonly Label volumeTitle, audioTitle;
        readonly Panel[] separators = new Panel[3];
        int[] displayedPresets = new int[0];
        int[] displayedVerticalPresets = new int[0];
        int displayedCurrent = -1;

        public QuickControlPage(float scale, Color surface, ISystemVolume systemVolume, IAudioControls audio, IPeripheralControls peripherals) {
            this.scale = scale;
            this.systemVolume = systemVolume;
            this.audio = audio;
            this.peripherals = peripherals;
            Name = "quickControlsPage";
            AccessibleName = "快捷控制";
            Size = new Size(Px(ViewWidth), Px(ViewHeight));
            AutoScroll = true;
            BackColor = surface;
            DoubleBuffered = true;
            headingFont = new Font("Microsoft YaHei UI", Px(15), FontStyle.Bold, GraphicsUnit.Pixel);
            labelFont = new Font("Microsoft YaHei UI", Px(14), FontStyle.Regular, GraphicsUnit.Pixel);
            numberFont = new Font("Segoe UI", Px(23), FontStyle.Bold, GraphicsUnit.Pixel);
            smallFont = new Font("Microsoft YaHei UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);

            dpiTitle = AddLabel("dpiTitle", "鼠标 DPI", headingFont, ContentAlignment.MiddleLeft);
            dpiStatus = AddLabel("dpiStatus", "", smallFont, ContentAlignment.MiddleLeft);
            dpiStatus.BackColor = Neutral;
            dpiStatus.ForeColor = Muted;
            dpiStatus.Padding = new Padding(Px(10), 0, Px(8), 0);
            dpiChoices = new Panel { Name = "dpiPresets", AccessibleName = "鼠标 DPI 档位", BackColor = BackColor, TabStop = false };
            Controls.Add(dpiChoices);

            volumeTitle = AddLabel("volumeTitle", "系统音量", headingFont, ContentAlignment.MiddleLeft);
            volumeStatus = AddLabel("volumeStatus", "", smallFont, ContentAlignment.MiddleLeft);
            volumeStatus.ForeColor = Muted;
            volumeValue = AddLabel("volumeValue", "—", numberFont, ContentAlignment.MiddleRight);
            volumeValue.ForeColor = Blue;
            volumeSpeaker = new VolumeSpeaker(scale, BackColor) { Name = "volumeSpeaker" };
            Controls.Add(volumeSpeaker);
            volume = new SystemVolumeSlider(scale, BackColor) { Name = "systemVolume", AccessibleName = "系统音量", Enabled = false };
            volume.ValueChanged += delegate { WriteVolume(); };
            volumeMonitor.Tick += delegate {
                ReadVolume();
                if (++audioTick % 3 == 0) ReadAudio(audioTick % 6 == 0);
            };
            peripheralMonitor.Tick += delegate {
                QueueMouseRead(-1);
                QueueKeyboardRead(null);
            };
            Controls.Add(volume);

            audioTitle = AddLabel("audioTitle", "音源与麦克风", headingFont, ContentAlignment.MiddleLeft);
            speakers = AddAction("demoSpeakers", "扬声器", 1);
            headset = AddAction("demoHeadset", "耳机", 2);
            microphone = AddAction("demoMicrophone", "麦克风", 3);
            microphone.AccessibleName = "麦克风静音";
            speakers.Click += delegate { SelectOutput(speakers, speakerTarget); };
            headset.Click += delegate { SelectOutput(headset, headsetTarget); };
            microphone.Click += delegate { ToggleMicrophone(); };

            lights = new LightingRow(scale, headingFont, smallFont, BackColor) {
                Name = "demoLighting", AccessibleName = "键盘灯光", TabIndex = 4, AutoCheck = false, Enabled = false
            };
            lights.Click += delegate { if (lightState != null) QueueKeyboardRead(!lightState.Enabled); };
            Controls.Add(lights);
            for (int i = 0; i < separators.Length; i++) {
                separators[i] = new Panel { BackColor = Rule, TabStop = false };
                Controls.Add(separators[i]);
            }

            SetMouseDpi();
            ShowLighting();
        }

        int Px(float value) { return (int)Math.Round(value * scale); }
        Rectangle Box(int x, int y, int w, int h) { return new Rectangle(Px(x), Px(y), Math.Max(1, Px(x+w)-Px(x)), Math.Max(1, Px(y+h)-Px(y))); }

        Label AddLabel(string name, string text, Font font, ContentAlignment align) {
            var label = new Label { Name = name, Text = text, Font = font, AutoSize = false, TextAlign = align,
                BackColor = BackColor, ForeColor = DeviceBatteryPanel.Ink, UseMnemonic = false, UseCompatibleTextRendering = true };
            Controls.Add(label);
            return label;
        }

        AudioButton AddAction(string name, string text, int tabIndex) {
            var button = new AudioButton(scale, BackColor) { Name = name, Text = text, Font = labelFont, TabIndex = tabIndex, Enabled = false };
            Controls.Add(button);
            return button;
        }

        void ReadAudio(bool listOutputs) {
            audioState = audio.Read(listOutputs);
            speakerTarget = audioState.Preferred(false, previousSpeaker);
            headsetTarget = audioState.Preferred(true, previousHeadset);
            ShowOutput(speakers, speakerTarget, "无可用扬声器");
            ShowOutput(headset, headsetTarget, "Windows 未识别到可切换的耳机输出");
            bool mic = !String.IsNullOrEmpty(audioState.MicrophoneId);
            microphone.Enabled = mic;
            microphone.Selected = mic && !audioState.MicrophoneMuted;
            microphone.Text = !mic ? "麦克风不可用" : audioState.MicrophoneMuted ? "麦克风 · 静音" : "麦克风 · 开启";
            SetHint(microphone, mic ? "切换当前默认麦克风的静音状态" : "没有可用的默认麦克风");
        }

        void SetHint(Control control, string text) {
            if (control.AccessibleDescription == text) return;
            control.AccessibleDescription = text;
            controlHints.SetToolTip(control, text);
        }

        void ShowOutput(AudioButton button, PlaybackDevice target, string absent) {
            bool selected = target != null && String.Equals(target.Id, audioState.DefaultOutput, StringComparison.OrdinalIgnoreCase);
            button.Enabled = target != null && audioState.CanSwitch;
            button.Selected = selected;
            if (button == headset) button.Text = target == null ? "耳机未连接" : "耳机";
            if (button == speakers) button.Text = target == null ? "扬声器不可用" : "扬声器";
            SetHint(button, target == null ? absent : target.Name + (selected ? " · 当前输出" : audioState.CanSwitch ? "" : " · 当前系统不支持直接切换"));
        }

        void SelectOutput(AudioButton button, PlaybackDevice target) {
            if (target == null || !button.Enabled) return;
            bool success = audio.SwitchOutput(target.Id);
            if (success) {
                if (target.IsHeadset) previousHeadset = target.Id;
                else previousSpeaker = target.Id;
            }
            ReadAudio(true);
            ReadVolume();
            if (!success) controlHints.Show("切换未确认，请检查设备连接", button, button.Width / 2, button.Height, 2400);
        }

        void ToggleMicrophone() {
            if (audioState == null || !microphone.Enabled) return;
            bool success = audio.ToggleMicrophone(audioState.MicrophoneId);
            ReadAudio(false);
            if (!success) controlHints.Show("麦克风状态未确认，请再试一次", microphone, microphone.Width / 2, microphone.Height, 2400);
        }

        internal void StartControls() {
            controlsOpen = true;
            ReadVolume();
            ReadAudio(true);
            audioTick = 0;
            volumeMonitor.Start();
            peripheralMonitor.Start();
            QueueMouseRead(-1);
            QueueKeyboardRead(null);
        }

        internal void StopControls() {
            controlsOpen = false;
            volumeMonitor.Stop();
            peripheralMonitor.Stop();
            queuedStage = -1;
            queuedLight = null;
            volume.CancelDrag();
        }

        void ReadVolume() {
            OutputVolumeState next;
            UpdateVolume(systemVolume.TryRead(out next) ? next : default(OutputVolumeState));
        }

        void WriteVolume() {
            if (applyingVolume || !volumeState.Available || !volume.Enabled || !Visible || volume.Value == volumeState.Percent) return;
            OutputVolumeState next;
            UpdateVolume(systemVolume.TrySet(volumeState.EndpointId, volume.Value / 100f, out next) ? next : default(OutputVolumeState));
        }

        void UpdateVolume(OutputVolumeState next) {
            if (!String.Equals(volumeState.EndpointId, next.EndpointId, StringComparison.OrdinalIgnoreCase)) volume.CancelDrag();
            volumeState = next;
            applyingVolume = true;
            try {
                volume.Enabled = next.Available;
                int percent = next.Available ? next.Percent : 0;
                if (volume.Value != percent) volume.Value = percent;
                string value = next.Available ? percent.ToString(CultureInfo.InvariantCulture) + "%" : "—";
                string status = !next.Available ? "音量不可用" : next.Muted ? "已静音" : "";
                if (volumeValue.Text != value) volumeValue.Text = value;
                if (volumeStatus.Text != status) volumeStatus.Text = status;
                if (volume.AccessibleDescription != status) volume.AccessibleDescription = status;
                volumeSpeaker.Level = next.Available && !next.Muted ? percent : 0;
            } finally { applyingVolume = false; }
        }

        internal void SetPeripherals(PanelReading mouse, PanelReading keyboard) {
            hasMouse = mouse != null && !String.IsNullOrEmpty(mouse.DeviceKey);
            hasKeyboard = keyboard != null && !String.IsNullOrEmpty(keyboard.DeviceKey);
            receiverOnly = mouse != null && mouse.Name == "罗技 LIGHTSPEED 接收器";
            string newMouse = hasMouse && PeripheralControls.SupportsMouseDpi(mouse.Name) ? mouse.DeviceKey : null;
            string newKeyboard = hasKeyboard && KeyboardLighting.Supports(keyboard.Name) ? keyboard.DeviceKey : null;
            bool mouseChanged = mouseKey != newMouse, keyboardChanged = keyboardKey != newKeyboard;
            if (mouseChanged) { mouseKey = newMouse; dpiState = null; dpiMessage = null; queuedStage = -1; }
            if (keyboardChanged) { keyboardKey = newKeyboard; lightState = null; lightMessage = null; queuedLight = null; }
            SetMouseDpi();
            ShowLighting();
            if (mouseChanged) QueueMouseRead(-1);
            if (keyboardChanged) QueueKeyboardRead(null);
        }

        void QueueMouseRead(int stage) {
            if (!controlsOpen || mouseKey == null || (stage >= 0 && (dpiState == null || !dpiState.CanSwitch))) return;
            if (mousePending) {
                if (stage >= 0 && !mouseWriting) { queuedStage = stage; UpdateDpiAvailability(); }
                return;
            }
            string key = mouseKey;
            MouseDpiState expected = dpiState;
            mousePending = true;
            mouseWriting = stage >= 0;
            if (stage >= 0) UpdateDpiAvailability();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                bool applied = false;
                MouseDpiState value = null;
                try {
                    value = stage < 0 ? peripherals.ReadMouse(key) : peripherals.SetDpi(key, expected, stage, out applied);
                } catch (Exception error) { BatteryReader.Log("Mouse control: " + error.Message); }
                Finish(delegate {
                    mousePending = mouseWriting = false;
                    if (key == mouseKey) {
                        if (value != null && value.ConfirmedStages > 0 && value.SameProfile(expected) &&
                                (stage < 0 || applied)) {
                            value.ConfirmedStages = Math.Max(value.ConfirmedStages, expected.ConfirmedStages);
                        }
                        dpiState = value;
                        dpiMessage = stage >= 0 && !applied ? "档位切换未确认，请重试" : null;
                        SetMouseDpi();
                        int next = queuedStage;
                        queuedStage = -1;
                        UpdateDpiAvailability();
                        if (dpiMessage != null && controlsOpen) controlHints.Show(dpiMessage, dpiTitle, 0, dpiTitle.Height, 2300);
                        if (expected != null && dpiState != null && dpiState.SameProfile(expected) && next >= 0 && next != dpiState.Index)
                            QueueMouseRead(next);
                    } else if (controlsOpen) QueueMouseRead(-1);
                });
            });
        }

        void QueueKeyboardRead(bool? enable) {
            if (!controlsOpen || keyboardKey == null || (enable.HasValue && (lightState == null || !lightState.CanSwitch))) return;
            if (keyboardPending) {
                if (enable.HasValue && !keyboardWriting) { queuedLight = enable; ShowLighting(); }
                return;
            }
            string key = keyboardKey;
            KeyboardLightState expected = lightState;
            keyboardPending = true;
            keyboardWriting = enable.HasValue;
            if (enable.HasValue) ShowLighting();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                bool applied = false;
                KeyboardLightState value = null;
                try {
                    value = !enable.HasValue ? peripherals.ReadKeyboard(key) : peripherals.SetLight(key, expected, enable.Value, out applied);
                } catch (Exception error) { BatteryReader.Log("Keyboard control: " + error.Message); }
                Finish(delegate {
                    keyboardPending = keyboardWriting = false;
                    if (key == keyboardKey) {
                        lightState = value;
                        lightMessage = enable.HasValue && !applied ? "灯光切换未确认" : null;
                        var next = queuedLight;
                        queuedLight = null;
                        ShowLighting();
                        if (lightMessage != null && controlsOpen) controlHints.Show(lightMessage, lights, lights.Width / 2, lights.Height, 2300);
                        if (next.HasValue && lightState != null && expected != null && lightState.Profile == expected.Profile &&
                            lightState.Effect == expected.Effect && lightState.Brightness == expected.Brightness && lightState.Enabled != next.Value)
                            QueueKeyboardRead(next);
                    } else if (controlsOpen) QueueKeyboardRead(null);
                });
            });
        }

        void Finish(Action completed) {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(completed); }
            catch (InvalidOperationException) {}
        }

        void ShowLighting() {
            bool known = lightState != null;
            bool readOnly = known && !lightState.CanSwitch;
            lights.Enabled = known && !readOnly && !keyboardWriting && !queuedLight.HasValue;
            bool on = known && lightState.Enabled;
            if (lights.Checked != on) lights.Checked = on;
            lights.Status = readOnly ? (on ? "亮 · 只读" : "灭 · 只读") :
                known ? (on ? "已开启" : "已关闭") :
                !hasKeyboard ? "未连接" : keyboardKey == null ? "暂不支持" : "未读到状态";
            SetHint(lights, lightMessage ?? (!hasKeyboard ? "未检测到外接键盘" :
                keyboardKey == null ? "当前键盘暂不支持读取灯光" :
                readOnly ? (on ? "键盘灯光已开启 · 只读" : "键盘灯光已关闭 · 只读") :
                known ? "切换按键背光；也可使用 Fn＋右 Shift" : "请唤醒键盘以读取灯光"));
        }

        void UpdateDpiAvailability() {
            foreach (AudioButton button in dpiChoices.Controls) {
                bool valid = dpiState != null && dpiState.CanSwitch && dpiState.Mode == 1 && dpiState.Index >= 0 && !mouseWriting && queuedStage < 0;
                bool readOnly = dpiState != null && !dpiState.CanSwitch;
                button.ShowReadOnly(readOnly);
                button.Enabled = valid;
            }
        }

        void SetMouseDpi() {
            int[] next = dpiState == null ? new int[0] : dpiState.Presets.Take(dpiState.VisibleStageCount).ToArray();
            int[] vertical = dpiState == null || dpiState.VerticalPresets == null ? next :
                dpiState.VerticalPresets.Take(dpiState.VisibleStageCount).ToArray();
            int current = dpiState == null ? -1 : dpiState.Index;
            string status = receiverOnly ? "请移动鼠标以读取 DPI 档位" : !hasMouse ? "未检测到外接鼠标" : mouseKey == null ? "当前鼠标暂不支持 DPI 读取" :
                dpiState != null && dpiState.Mode == 2 ? "鼠标由 G HUB 控制，未使用板载档位" :
                "暂未读取到可用的 DPI 档位";
            if (dpiState != null && !dpiState.CanSwitch && next.Length == 0 && dpiState.Current > 0) {
                string value = dpiState.Current.ToString(CultureInfo.InvariantCulture);
                if (dpiState.CurrentVertical > 0 && dpiState.CurrentVertical != dpiState.Current)
                    value += "×" + dpiState.CurrentVertical.ToString(CultureInfo.InvariantCulture);
                status = "当前 " + value + " DPI · 暂未读到档位";
            } else if (dpiState != null && !dpiState.CanSwitch && next.Length == 0 && current >= 0) {
                status = "当前第 " + (current+1).ToString(CultureInfo.InvariantCulture) + " 档";
            }
            dpiTitle.Text = dpiState != null && !dpiState.CanSwitch ? "鼠标 DPI · 只读" :
                dpiState != null && dpiState.ConfirmedStages > 0 && dpiState.ConfirmedStages < dpiState.Presets.Length
                ? "鼠标 DPI · 已确认 " + next.Count(value => value > 0) + " 档" : "鼠标 DPI";
            if (next.SequenceEqual(displayedPresets) && vertical.SequenceEqual(displayedVerticalPresets) &&
                current == displayedCurrent && displayedDpiStatus == status) {
                UpdateDpiAvailability();
                return;
            }
            displayedDpiStatus = status;
            displayedCurrent = current;
            displayedPresets = next;
            displayedVerticalPresets = vertical;
            int count = next.Count(value => value > 0);
            int extra = ((Math.Max(1, count) - 1) / 5) * 39;

            SuspendLayout();
            try {
                AutoScrollPosition = Point.Empty;
                while (dpiChoices.Controls.Count > 0) dpiChoices.Controls[0].Dispose();
                int width = ViewWidth;
                if (extra > 0) {
                    // Leave enough space even if the active display uses larger native scroll bars.
                    width -= Math.Max(24, (int)Math.Ceiling(SystemInformation.VerticalScrollBarWidth / scale) + 6);
                }
                int buttonAreaHeight = 33 + extra;
                dpiTitle.Bounds = Box(0, 6, width, 23);
                dpiChoices.Bounds = Box(0, 34, width, buttonAreaHeight);
                dpiStatus.Bounds = Box(0, 34, width, 33);
                dpiStatus.Text = status;
                dpiStatus.Visible = count == 0;
                dpiChoices.Visible = count != 0;
                if (count > 0) {
                    int columns = Math.Min(5, count);
                    int itemWidth = Math.Min(88, (width - Gap * (columns - 1)) / columns);
                    int displayed = 0;
                    for (int i = 0; i < next.Length; i++) {
                        int value = next[i];
                        if (value <= 0) continue;
                        int slot = i, position = displayed++;
                        bool active = i == current;
                        string text = value.ToString(CultureInfo.InvariantCulture);
                        if (vertical[i] != value) text += "×" + vertical[i].ToString(CultureInfo.InvariantCulture);
                        var option = new AudioButton(scale, BackColor) { Name = "dpi" + i, Text = text,
                            AccessibleName = text + " DPI" + (active ? " · 当前档位" : ""), Font = labelFont,
                            Selected = active, StrongSelection = true,
                            Bounds = Box((position % columns) * (itemWidth + Gap), (position / columns) * 39, itemWidth, 33) };
                        option.Click += delegate { if (slot != displayedCurrent) QueueMouseRead(slot); };
                        dpiChoices.Controls.Add(option);
                    }
                }

                separators[0].Bounds = Box(0, 78 + extra, width, 1);
                volumeTitle.Bounds = Box(0, 84 + extra, 74, 26);
                volumeStatus.Bounds = Box(80, 84 + extra, width - 164, 26);
                volumeValue.Bounds = Box(width - 80, 83 + extra, 80, 28);
                volumeSpeaker.Bounds = Box(0, 110 + extra, 40, 30);
                volume.Bounds = Box(49, 110 + extra, width - 49, 30);
                separators[1].Bounds = Box(0, 145 + extra, width, 1);
                audioTitle.Bounds = Box(0, 151 + extra, width, 23);
                int audioWidth = (width - 2 * Gap) / 3;
                speakers.Bounds = Box(0, 181 + extra, audioWidth, 43);
                headset.Bounds = Box(audioWidth + Gap, 181 + extra, audioWidth, 43);
                microphone.Bounds = Box(2 * (audioWidth + Gap), 181 + extra, width - 2 * (audioWidth + Gap), 43);
                separators[2].Bounds = Box(0, 235 + extra, width, 1);
                lights.Bounds = Box(0, 239 + extra, width, 30);
            } finally { ResumeLayout(); }
            UpdateDpiAvailability();
            PerformLayout();
        }

        static void PaintText(Graphics g, string text, Font font, Rectangle bounds, Color color, StringAlignment alignment) {
            var state = g.Save();
            try {
                g.SetClip(bounds, CombineMode.Intersect);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var brush = new SolidBrush(color))
                using (var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter }) {
                    g.DrawString(text, font, brush, bounds, format);
                }
            } finally { g.Restore(state); }
        }

        // Use the same clipped, opaque surface for the background and all foreground marks.
        static void PaintSurface(PaintEventArgs e, Rectangle bounds, Color color) {
            e.Graphics.SetClip(bounds, CombineMode.Intersect);
            e.Graphics.SetClip(e.ClipRectangle, CombineMode.Intersect);
            using (var fill = new SolidBrush(color)) e.Graphics.FillRectangle(fill, bounds);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        }

        static void PaintFocus(Graphics g, Rectangle bounds) {
            bounds.Inflate(-2, -2);
            if (bounds.Width > 0 && bounds.Height > 0)
                using (var pen = new Pen(SelectedText) { DashStyle = DashStyle.Dot }) g.DrawRectangle(pen, bounds);
        }

        protected override void Dispose(bool disposing) {
            if (disposing && !IsDisposed) {
                volumeMonitor.Dispose();
                peripheralMonitor.Dispose();
                controlHints.Dispose();
                systemVolume.Dispose();
                audio.Dispose();
                base.Dispose(disposing);
                headingFont.Dispose(); labelFont.Dispose(); numberFont.Dispose(); smallFont.Dispose();
                return;
            }
            base.Dispose(disposing);
        }

        sealed class VolumeSpeaker : Control {
            static readonly RectangleF[] Waves = {
                new RectangleF(14, 8, 12, 12),
                new RectangleF(12, 4, 20, 20),
                new RectangleF(10, 0, 28, 28)
            };
            static readonly PointF[] Body = {
                new PointF(0.5f, 10), new PointF(2, 8), new PointF(7.5f, 8), new PointF(16, 2),
                new PointF(17.5f, 3.5f), new PointF(17.5f, 24.5f), new PointF(16, 26),
                new PointF(7.5f, 20), new PointF(2, 20), new PointF(0.5f, 18)
            };
            readonly float scale;
            int waves;

            internal VolumeSpeaker(float scale, Color surface) {
                this.scale = scale;
                TabStop = false;
                BackColor = surface;
                SetStyle(ControlStyles.Selectable, false);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            internal int Level {
                set {
                    int next = value == 0 ? 0 : value <= 33 ? 1 : value <= 66 ? 2 : 3;
                    if (next == waves) return;
                    waves = next;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e) {
                PaintSurface(e, ClientRectangle, BackColor);
                var g = e.Graphics;
                g.TranslateTransform(0, (Height - 28f * scale) / 2f);
                g.ScaleTransform(scale, scale);
                using (var fill = new SolidBrush(Blue)) g.FillPolygon(fill, Body);
                using (var stroke = new Pen(Blue, 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                    for (int i = 0; i < waves; i++) g.DrawArc(stroke, Waves[i], -46, 92);
                }
            }
        }

        sealed class SystemVolumeSlider : TrackBar {
            readonly float scale;
            bool dragging;

            internal SystemVolumeSlider(float scale, Color surface) {
                this.scale = scale;
                Minimum = 0; Maximum = 100; SmallChange = 1; LargeChange = 10;
                TickStyle = TickStyle.None; AutoSize = false; BackColor = surface;
                SetStyle(ControlStyles.UserMouse, true);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnValueChanged(EventArgs e) {
                base.OnValueChanged(e);
                Invalidate();
            }

            float Inset { get { return 10f * scale; } }

            void SetValueAt(int x) {
                float span = Math.Max(1, ClientSize.Width - 2 * Inset);
                Value = Math.Max(Minimum, Math.Min(Maximum, (int)Math.Round((x - Inset) * Maximum / span)));
            }

            protected override void OnMouseDown(MouseEventArgs e) {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || !Enabled) return;
                Focus();
                Capture = true;
                dragging = Capture;
                SetValueAt(e.X);
            }

            protected override void OnMouseMove(MouseEventArgs e) {
                base.OnMouseMove(e);
                if (dragging && Capture && (e.Button & MouseButtons.Left) != 0) SetValueAt(e.X);
            }

            protected override void OnMouseUp(MouseEventArgs e) {
                if (e.Button == MouseButtons.Left && dragging) CancelDrag();
                base.OnMouseUp(e);
            }

            internal void CancelDrag() {
                if (!dragging) return;
                dragging = false;
                Capture = false;
            }

            protected override void OnMouseCaptureChanged(EventArgs e) {
                if (!Capture) dragging = false;
                base.OnMouseCaptureChanged(e);
            }

            protected override void OnPaint(PaintEventArgs e) {
                PaintSurface(e, ClientRectangle, BackColor);
                var g = e.Graphics;
                float inset = Inset, width = ClientSize.Width - 2 * inset, mid = ClientSize.Height / 2f;
                float knob = 9.5f * scale, thickness = 6f * scale;
                float position = inset + width * Value / Maximum;
                using (var track = new Pen(Color.FromArgb(220, 229, 238), thickness) {
                    StartCap = LineCap.Round, EndCap = LineCap.Round
                }) {
                    g.DrawLine(track, inset, mid, inset + width, mid);
                    if (Enabled && Value > 0) { track.Color = Blue; g.DrawLine(track, inset, mid, position, mid); }
                }
                using (var fill = new SolidBrush(Enabled ? Blue : Muted)) {
                    g.FillEllipse(fill, position - knob, mid - knob, 2 * knob, 2 * knob);
                    g.FillEllipse(Brushes.White, position - knob + scale, mid - knob + scale, 2 * (knob - scale), 2 * (knob - scale));
                    g.FillEllipse(fill, position - knob + 3 * scale, mid - knob + 3 * scale, 2 * (knob - 3 * scale), 2 * (knob - 3 * scale));
                }
                if (Focused && ShowFocusCues) PaintFocus(g, ClientRectangle);
            }
        }

        sealed class AudioButton : Button {
            readonly float scale;
            bool selected, hover, readOnly;
            internal bool StrongSelection;

            internal AudioButton(float scale, Color surface) {
                this.scale = scale;
                BackColor = surface;
                FlatStyle = FlatStyle.Flat;
                UseVisualStyleBackColor = false;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            internal bool Selected {
                set { if (selected != value) { selected = value; Invalidate(); } }
            }

            internal void ShowReadOnly(bool value) {
                if (readOnly == value) return;
                readOnly = value;
                Cursor = value ? Cursors.Default : Cursors.Hand;
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e) {
                PaintSurface(e, ClientRectangle, BackColor);
                var g = e.Graphics;
                bool active = selected && StrongSelection && (Enabled || readOnly);
                Color background = active ? Blue : !Enabled ? Neutral : hover ? Hover : selected ? QuickControlPage.Selected : Neutral;
                Color textColor = active ? Color.White : !Enabled ? Muted : selected ? SelectedText : DeviceBatteryPanel.Ink;
                using (var path = TrayDrawing.RoundedRect(.5f, .5f, Width - 1f, Height - 1f, Math.Max(1, 8f * scale)))
                using (var fill = new SolidBrush(background))
                using (var edge = new Pen(selected ? SelectedBorder : Border)) {
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                }
                Rectangle text = ClientRectangle;
                text.Inflate(-(int)Math.Round(5 * scale), -1);
                PaintText(g, Text, Font, text, textColor, StringAlignment.Center);
                if (Focused && ShowFocusCues) PaintFocus(g, text);
            }
        }

        sealed class LightingRow : CheckBox {
            readonly float scale;
            readonly Font titleFont, stateFont;
            bool hover;
            string status = "";

            internal LightingRow(float scale, Font titleFont, Font stateFont, Color surface) {
                this.scale = scale;
                this.titleFont = titleFont;
                this.stateFont = stateFont;
                BackColor = surface;
                Cursor = Cursors.Hand;
                AutoSize = false;
                FlatStyle = FlatStyle.Flat;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            int Px(float value) { return Math.Max(1, (int)Math.Round(value * scale)); }

            internal string Status { set { if (status != value) { status = value; Invalidate(); } } }

            protected override void OnCheckedChanged(EventArgs e) {
                base.OnCheckedChanged(e);
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e) {
                base.OnMouseEnter(e);
                hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e) {
                base.OnMouseLeave(e);
                hover = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e) {
                // Draw the whole row through one graphics context; no text-bearing child can paint under the switch.
                PaintSurface(e, ClientRectangle, BackColor);
                var g = e.Graphics;
                int trackWidth = Px(42), trackHeight = Px(23);
                int trackX = Width - trackWidth - Px(3), trackY = (Height - trackHeight) / 2;
                int inset = Px(3), knob = trackHeight - 2 * inset;
                int stateEdge = trackX - Px(12), stateStart = stateEdge - Px(66);
                var titleArea = new Rectangle(0, 0, Math.Max(1, stateStart - Px(12)), Height);
                var stateArea = new Rectangle(stateStart, 0, stateEdge - stateStart, Height);
                PaintText(g, "键盘灯光", titleFont, titleArea, DeviceBatteryPanel.Ink, StringAlignment.Near);
                PaintText(g, status, stateFont, stateArea, Enabled && Checked ? SelectedText : Muted, StringAlignment.Far);

                Color color = !Enabled ? Border : Checked ? (hover ? SelectedText : Blue) : (hover ? DeviceBatteryPanel.Ink : Muted);
                using (var path = TrayDrawing.RoundedRect(trackX, trackY, trackWidth, trackHeight, trackHeight / 2f))
                using (var fill = new SolidBrush(color)) g.FillPath(fill, path);
                g.FillEllipse(Brushes.White, Checked ? trackX + trackWidth - inset - knob : trackX + inset, trackY + inset, knob, knob);
                if (Focused && ShowFocusCues) PaintFocus(g, new Rectangle(trackX - 2, 0, trackWidth + 4, Height));
            }
        }
    }
}
