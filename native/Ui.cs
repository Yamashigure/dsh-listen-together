// 桌面上的那只小悬浮窗：曲目、波形、暂停、音量、锁定、外观。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ListenTogether
{
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    class TaskbarListCom
    {
    }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarListCom
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    class FloatingForm : Form
    {
        const int BASE_W = 356;
        const int BASE_H = 116;
        readonly State state;
        readonly Action<string> emit;
        readonly System.Windows.Forms.Timer paintTimer;
        readonly string settingsFile;
        bool dragging;
        bool volumeDragging;
        Point dragOrigin;
        bool resizing;
        int resizeEdges;
        Point resizeStart;
        Rectangle resizeFrom;
        float scale = 1f;
        bool startHidden;
        bool locked;
        float opacityLevel = 0.97f;
        Color waveColor = Color.FromArgb(143, 168, 255);
        int waveOpacity = 78;
        string backgroundPath = "";
        Image backgroundImage;
        int bgOffsetX;
        int bgOffsetY;
        bool editingBackground;
        bool bgDragging;
        Point bgDragStart;
        int bgStartOffsetX;
        int bgStartOffsetY;
        bool lockBeforeEdit;
        static readonly Stopwatch titleClock = Stopwatch.StartNew();
        string titlePaintKey = "";
        long titleScrollSince;

        public FloatingForm(State state, Action<string> emit, bool startHidden)
        {
            this.state = state;
            this.emit = emit;
            this.startHidden = startHidden;
            settingsFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dsh-listen-together", "ui.ini");

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(14, 16, 21);
            Text = "一起听歌";
            try { Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point); } catch { }
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            LoadSettings();
            Resize += delegate { RoundCorners(); ClampBackgroundOffset(); };

            paintTimer = new System.Windows.Forms.Timer();
            paintTimer.Interval = 33;
            paintTimer.Tick += delegate { if (Visible) Invalidate(); };
            paintTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RoundCorners();
            HideTaskbarButton();
            try
            {
                Native.RegisterHotKey(Handle, 1, 0x0002 | 0x0001, (uint)Keys.M); // Ctrl+Alt+M 显示/隐藏
                Native.RegisterHotKey(Handle, 2, 0x0002 | 0x0001, (uint)Keys.L); // Ctrl+Alt+L 锁定/解锁
            }
            catch { }
            if (startHidden)
            {
                Hide();
                SaveSettings();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideTaskbarButton();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) HideTaskbarButton();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            HideTaskbarButton();
        }

        ITaskbarListCom taskbar;

        void HideTaskbarButton()
        {
            if (Handle == IntPtr.Zero) return;
            try
            {
                if (taskbar == null)
                {
                    taskbar = (ITaskbarListCom)new TaskbarListCom();
                    taskbar.HrInit();
                }
                taskbar.DeleteTab(Handle);
            }
            catch { taskbar = null; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                SaveSettings();
                emit("{\"type\":\"ui\",\"action\":\"hide\"}");
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) ToggleVisible();
                else if (id == 2) ToggleLock();
                return;
            }
            if (m.Msg == 0x0084 && locked) // WM_NCHITTEST
            {
                int lp = m.LParam.ToInt32();
                int sx = (short)(lp & 0xffff);
                int sy = (short)((lp >> 16) & 0xffff);
                Point p = PointToClient(new Point(sx, sy));
                m.Result = LockRect().Contains(p) ? (IntPtr)1 : (IntPtr)(-1);
                return;
            }
            base.WndProc(ref m);
        }

        public void ToggleVisible()
        {
            if (Visible)
            {
                Hide();
                SaveSettings();
                emit("{\"type\":\"ui\",\"action\":\"hide\"}");
            }
            else
            {
                Show();
                BringToFront();
                emit("{\"type\":\"ui\",\"action\":\"show\"}");
            }
        }

        public void SetVisible(bool visible)
        {
            if (visible) { if (!Visible) { Show(); BringToFront(); emit("{\"type\":\"ui\",\"action\":\"show\"}"); } }
            else { if (Visible) { Hide(); emit("{\"type\":\"ui\",\"action\":\"hide\"}"); } }
            SaveSettings();
        }

        public void ToggleLock()
        {
            locked = !locked;
            SaveSettings();
            Invalidate();
            emit(SettingsJson());
        }

        public void SetLocked(bool value)
        {
            if (locked == value) return;
            locked = value;
            SaveSettings();
            Invalidate();
            emit(SettingsJson());
        }

        public void SetOpacity(float value)
        {
            opacityLevel = Math.Max(0.25f, Math.Min(1f, value));
            Opacity = opacityLevel;
            SaveSettings();
            Invalidate();
            emit(SettingsJson());
        }

        public void SetSize(int width, int height)
        {
            width = Math.Max((int)Math.Round(BASE_W * scale), Math.Min(1200, width));
            height = Math.Max((int)Math.Round(BASE_H * scale), Math.Min(600, height));
            Width = width;
            Height = height;
            RoundCorners();
            SaveSettings();
            Invalidate();
            emit(SettingsJson());
        }

        public void SetBackground(string path)
        {
            backgroundPath = string.IsNullOrEmpty(path) ? "" : path;
            editingBackground = false;
            bgOffsetX = 0;
            bgOffsetY = 0;
            LoadBackground();
            ClampBackgroundOffset();
            SaveSettings();
            Invalidate();
            emit(SettingsJson());
        }

        public void EnterBackgroundEdit()
        {
            if (backgroundImage == null) return;
            lockBeforeEdit = locked;
            if (locked) SetLocked(false);
            editingBackground = true;
            bgDragging = false;
            ClampBackgroundOffset();
            Invalidate();
            emit("{\"type\":\"ui\",\"action\":\"bg-edit\"}");
        }

        public void ConfirmBackgroundEdit()
        {
            if (!editingBackground) return;
            editingBackground = false;
            bgDragging = false;
            Capture = false;
            ClampBackgroundOffset();
            SaveSettings();
            Invalidate();
            if (lockBeforeEdit) SetLocked(true);
            emit("{\"type\":\"ui\",\"action\":\"bg-done\"}");
        }

        public void SetWave(string color, int opacity)
        {
            Color parsed;
            if (TryParseColor(color, out parsed)) waveColor = parsed;
            waveOpacity = Math.Max(0, Math.Min(100, opacity));
            SaveSettings();
            Invalidate();
            emit(SettingsJson());
        }

        static bool TryParseColor(string text, out Color color)
        {
            color = Color.Empty;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            if (t.StartsWith("#", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.Length != 6) return false;
            int value;
            if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            color = Color.FromArgb(255, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
            return true;
        }

        static string HexOf(Color color)
        {
            return "#" + color.R.ToString("X2", CultureInfo.InvariantCulture)
                + color.G.ToString("X2", CultureInfo.InvariantCulture)
                + color.B.ToString("X2", CultureInfo.InvariantCulture);
        }

        void ClampBackgroundOffset()
        {
            if (backgroundImage == null) return;
            float ar = backgroundImage.Width / (float)backgroundImage.Height;
            if (ar > Width / (float)Height)
            {
                int w = (int)(Height * ar);
                int room = Math.Max(0, w - Width);
                bgOffsetX = Math.Max(-room / 2, Math.Min(room / 2, bgOffsetX));
                bgOffsetY = 0;
            }
            else
            {
                int h = (int)(Width / ar);
                int room = Math.Max(0, h - Height);
                bgOffsetY = Math.Max(-room / 2, Math.Min(room / 2, bgOffsetY));
                bgOffsetX = 0;
            }
        }

        Rectangle BackgroundConfirmRect()
        {
            float s = scale;
            int pad = (int)Math.Round(16 * s);
            int w = (int)Math.Round(64 * s);
            int h = (int)Math.Round(26 * s);
            return new Rectangle(Width - pad - w - (int)Math.Round(28 * s), (int)Math.Round(10 * s), w, h);
        }

        public void PickBackground()
        {
            try
            {
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "选一张背景图";
                    dialog.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif";
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    string chosen = dialog.FileName;
                    string dir = Path.GetDirectoryName(settingsFile) ?? "";
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string ext = Path.GetExtension(chosen).ToLowerInvariant();
                    if (ext == ".jpeg") ext = ".jpg";
                    if (ext != ".png" && ext != ".jpg" && ext != ".bmp" && ext != ".gif") ext = ".jpg";
                    string target = Path.Combine(dir, "background" + ext);
                    File.Copy(chosen, target, true);
                    SetBackground(target);
                }
            }
            catch (Exception error)
            {
                emit("{\"type\":\"error\",\"message\":" + J.S("pickbg: " + error.Message) + "}");
            }
        }

        void LoadBackground()
        {
            if (backgroundImage != null)
            {
                try { backgroundImage.Dispose(); } catch { }
                backgroundImage = null;
            }
            if (string.IsNullOrEmpty(backgroundPath) || !File.Exists(backgroundPath)) return;
            try
            {
                using (var source = Image.FromFile(backgroundPath))
                    backgroundImage = new Bitmap(source);
            }
            catch { backgroundImage = null; }
            ClampBackgroundOffset();
        }

        public string SettingsJson()
        {
            return "{\"type\":\"settings\",\"width\":" + Width + ",\"height\":" + Height
                + ",\"opacity\":" + J.N(opacityLevel, 2)
                + ",\"locked\":" + (locked ? "true" : "false")
                + ",\"background\":" + J.S(backgroundPath)
                + ",\"bgx\":" + bgOffsetX + ",\"bgy\":" + bgOffsetY
                + ",\"waveColor\":" + J.S(HexOf(waveColor)) + ",\"waveOpacity\":" + waveOpacity + "}";
        }

        public void EmitSettings()
        {
            emit(SettingsJson());
        }

        void RoundCorners()
        {
            int radius = (int)Math.Round(16 * Math.Max(0.6f, scale));
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, radius, radius, 180, 90);
                path.AddArc(Width - radius, 0, radius, radius, 270, 90);
                path.AddArc(Width - radius, Height - radius, radius, radius, 0, 90);
                path.AddArc(0, Height - radius, radius, radius, 90, 90);
                path.CloseAllFigures();
                Region = new Region(path);
            }
        }

        void LoadSettings()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; } catch { }
            if (scale <= 0.5f || scale > 4f) scale = 1f;
            int w = (int)Math.Round(BASE_W * scale), h = (int)Math.Round(BASE_H * scale);
            var screen = Screen.PrimaryScreen.WorkingArea;
            int x = screen.Right - w - 28;
            int y = screen.Bottom - h - 28;
            try
            {
                if (File.Exists(settingsFile))
                {
                    foreach (string raw in File.ReadAllLines(settingsFile))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = raw.Substring(0, eq).Trim();
                        string value = raw.Substring(eq + 1).Trim();
                        switch (key)
                        {
                            case "x": int px; if (int.TryParse(value, out px)) x = px; break;
                            case "y": int py; if (int.TryParse(value, out py)) y = py; break;
                            case "width": int pw; if (int.TryParse(value, out pw)) w = pw; break;
                            case "height": int ph; if (int.TryParse(value, out ph)) h = ph; break;
                            case "opacity": float op; if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out op)) opacityLevel = Math.Max(0.25f, Math.Min(1f, op)); break;
                            case "locked": locked = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                            case "background": backgroundPath = value; break;
                            case "bgx": int bx; if (int.TryParse(value, out bx)) bgOffsetX = bx; break;
                            case "bgy": int by; if (int.TryParse(value, out by)) bgOffsetY = by; break;
                            case "wavecolor": Color wc; if (TryParseColor(value, out wc)) waveColor = wc; break;
                            case "waveopacity": int wo; if (int.TryParse(value, out wo)) waveOpacity = Math.Max(0, Math.Min(100, wo)); break;
                        }
                    }
                }
            }
            catch { }
            Width = Math.Max((int)Math.Round(BASE_W * scale), Math.Min(1200, w));
            Height = Math.Max((int)Math.Round(BASE_H * scale), Math.Min(600, h));
            var bounds = Screen.FromPoint(new Point(x + Width / 2, y + Height / 2)).WorkingArea;
            x = Math.Max(bounds.Left, Math.Min(x, bounds.Right - Width));
            y = Math.Max(bounds.Top, Math.Min(y, bounds.Bottom - Height));
            Location = new Point(x, y);
            Opacity = opacityLevel;
            LoadBackground();
        }

        public void SaveSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(settingsFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string text = string.Join("\r\n", new string[] {
                    "x=" + Location.X,
                    "y=" + Location.Y,
                    "width=" + Width,
                    "height=" + Height,
                    "opacity=" + opacityLevel.ToString("F2", CultureInfo.InvariantCulture),
                    "locked=" + (locked ? "1" : "0"),
                    "background=" + backgroundPath,
                    "bgx=" + bgOffsetX,
                    "bgy=" + bgOffsetY,
                    "wavecolor=" + HexOf(waveColor),
                    "waveopacity=" + waveOpacity,
                });
                File.WriteAllText(settingsFile, text);
            }
            catch { }
        }

        Rectangle LockRect()
        {
            float s = scale;
            int pad = (int)Math.Round(16 * s);
            int closeX = Width - pad - (int)Math.Round(2 * s);
            int closeY = (int)Math.Round(20 * s);
            int lockX = closeX - (int)Math.Round(22 * s);
            return new Rectangle(lockX - (int)(10 * s), closeY - (int)(10 * s), (int)(20 * s), (int)(20 * s));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float dpi = g.DpiX / 96f;
            if (scale <= 0.5f) scale = dpi;
            float fs = 1f;

            string title, app;
            float[] wave;
            bool playing;
            int volume;
            bool muted;
            int head;
            lock (state.Gate)
            {
                title = state.Title ?? "";
                app = state.App ?? "";
                playing = state.Playing;
                volume = state.Volume;
                muted = state.Muted;
                wave = (float[])state.Wave.Clone();
                head = state.WaveHead;
            }

            int pad = (int)Math.Round(16 * scale);
            int titleX = pad;
            int titleY = (int)Math.Round(12 * scale);
            int titleW = Width - pad * 2 - (int)Math.Round(52 * scale);
            int waveY = (int)Math.Round(50 * scale);
            int controlsY = Height - (int)Math.Round(34 * scale);
            int waveH = Math.Max((int)Math.Round(24 * scale), controlsY - waveY - (int)Math.Round(8 * scale));
            int waveW = Width - pad * 2;
            int playR = (int)Math.Round(11 * scale);
            int playX = pad + playR;
            int playY = controlsY + playR - (int)Math.Round(2 * scale);

            if (backgroundImage != null)
            {
                float ar = backgroundImage.Width / (float)backgroundImage.Height;
                Rectangle dest;
                if (ar > Width / (float)Height)
                {
                    int w = (int)(Height * ar);
                    int x = (Width - w) / 2 + bgOffsetX;
                    x = Math.Max(Width - w, Math.Min(0, x));
                    dest = new Rectangle(x, 0, w, Height);
                }
                else
                {
                    int h = (int)(Width / ar);
                    int y = (Height - h) / 2 + bgOffsetY;
                    y = Math.Max(Height - h, Math.Min(0, y));
                    dest = new Rectangle(0, y, Width, h);
                }
                g.DrawImage(backgroundImage, dest);
                using (var veil = new SolidBrush(Color.FromArgb(150, 14, 16, 21)))
                    g.FillRectangle(veil, ClientRectangle);
            }
            else
            {
                using (var bg = new SolidBrush(Color.FromArgb(14, 16, 21)))
                    g.FillRectangle(bg, ClientRectangle);
            }

            using (var border = new Pen(Color.FromArgb(38, 44, 58), 1f))
            using (var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), (int)Math.Round(16 * scale)))
                g.DrawPath(border, path);

            int dotY = titleY + (int)Math.Round(8 * scale);
            using (var dot = new SolidBrush(playing ? Color.FromArgb(143, 168, 255) : Color.FromArgb(90, 96, 110)))
                g.FillEllipse(dot, pad, dotY, (int)Math.Round(7 * scale), (int)Math.Round(7 * scale));

            string shownTitle = title.Length == 0 ? "还没听到声音" : title;
            using (var font = new Font("Microsoft YaHei UI", 9.5f * fs, FontStyle.Regular, GraphicsUnit.Point))
            using (var brush = new SolidBrush(Color.FromArgb(232, 235, 242)))
            {
                var titleRect = new RectangleF(titleX + 14 * scale, titleY - 2 * scale, titleW, 24 * fs * 1.35f);
                float measured = g.MeasureString(shownTitle, font, 100000, StringFormat.GenericTypographic).Width;
                if (!string.Equals(titlePaintKey, shownTitle, StringComparison.Ordinal))
                {
                    titlePaintKey = shownTitle;
                    titleScrollSince = titleClock.ElapsedMilliseconds;
                }
                float offset = 0f;
                if (measured > titleRect.Width)
                {
                    float travel = measured - titleRect.Width + (float)Math.Round(10 * scale);
                    float speed = 30f * scale;
                    float pause = 1.1f;
                    float scrollTime = travel / speed;
                    float cycle = pause * 2 + scrollTime;
                    float elapsed = (titleClock.ElapsedMilliseconds - titleScrollSince) / 1000f;
                    float t = elapsed % cycle;
                    if (t > pause && t <= pause + scrollTime) offset = (t - pause) * speed;
                    else if (t > pause + scrollTime) offset = travel;
                }
                var saved = g.Save();
                g.SetClip(titleRect);
                g.DrawString(shownTitle, font, brush, new PointF(titleRect.X - offset, titleRect.Y), StringFormat.GenericTypographic);
                g.Restore(saved);
            }

            string appLabel = app.Length == 0 ? "默认输出" : PrettyApp(app);
            string sub = appLabel + (playing ? "  ·  正在播放" : "  ·  暂停中") + "  ·  AI 在听" + (locked ? "  ·  已锁定" : "");
            using (var font = new Font("Microsoft YaHei UI", 7.5f * fs, FontStyle.Regular, GraphicsUnit.Point))
            using (var brush = new SolidBrush(Color.FromArgb(112, 120, 138)))
                g.DrawString(sub, font, brush, new PointF(titleX + 14 * scale, titleY + 18 * scale));

            int bars = 48;
            float barW = (float)waveW / bars;
            for (int i = 0; i < bars; i++)
            {
                int src = (head - bars + i + wave.Length * 2) % wave.Length;
                float v = wave[src];
                float h = Math.Max(2 * scale, Math.Min(waveH, (float)(Math.Sqrt(v) * waveH * 1.9)));
                int x = (int)(pad + i * barW);
                int yy = waveY + (waveH - (int)h) / 2;
                int alpha = (int)Math.Round((playing ? waveOpacity : waveOpacity * 0.35) * 2.55);
                alpha = Math.Max(0, Math.Min(255, alpha));
                using (var pen = new Pen(Color.FromArgb(alpha, waveColor), Math.Max(1.4f, barW * 0.46f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, x, yy + h, x, yy + 1);
                }
            }

            using (var circle = new SolidBrush(Color.FromArgb(24, 28, 37)))
                g.FillEllipse(circle, playX - playR, playY - playR, playR * 2, playR * 2);
            using (var accent = new SolidBrush(Color.FromArgb(143, 168, 255)))
            {
                if (playing)
                {
                    g.FillRectangle(accent, playX - 4 * scale, playY - 6 * scale, 3 * scale, 12 * scale);
                    g.FillRectangle(accent, playX + 1 * scale, playY - 6 * scale, 3 * scale, 12 * scale);
                }
                else
                {
                    PointF[] tri = {
                        new PointF(playX - 4 * scale, playY - 7 * scale),
                        new PointF(playX + 7 * scale, playY),
                        new PointF(playX - 4 * scale, playY + 7 * scale)
                    };
                    g.FillPolygon(accent, tri);
                }
            }

            int speakerX = pad + playR * 2 + (int)Math.Round(14 * scale);
            int volX0 = speakerX + (int)Math.Round(16 * scale);
            int volX1 = Width - pad - (int)Math.Round(42 * scale);
            int volY = playY;
            using (var speaker = new SolidBrush(Color.FromArgb(150, 157, 175)))
            {
                g.FillRectangle(speaker, speakerX, volY - 4 * scale, 3 * scale, 8 * scale);
                PointF[] cone = {
                    new PointF(speakerX + 3 * scale, volY - 4 * scale),
                    new PointF(speakerX + 9 * scale, volY - 8 * scale),
                    new PointF(speakerX + 9 * scale, volY + 8 * scale),
                    new PointF(speakerX + 3 * scale, volY + 4 * scale)
                };
                g.FillPolygon(speaker, cone);
            }
            using (var track = new Pen(Color.FromArgb(38, 44, 58), 3f))
            {
                track.StartCap = LineCap.Round;
                track.EndCap = LineCap.Round;
                g.DrawLine(track, volX0, volY, volX1, volY);
            }
            int knobX = volX0 + (int)Math.Round((volX1 - volX0) * (muted ? 0 : volume / 100f));
            using (var fill = new Pen(Color.FromArgb(143, 168, 255), 3f))
            {
                fill.StartCap = LineCap.Round;
                fill.EndCap = LineCap.Round;
                g.DrawLine(fill, volX0, volY, knobX, volY);
            }
            using (var knob = new SolidBrush(Color.FromArgb(232, 235, 242)))
                g.FillEllipse(knob, knobX - 4 * scale, volY - 4 * scale, 8 * scale, 8 * scale);
            using (var font = new Font("Segoe UI", 7f * fs, FontStyle.Regular, GraphicsUnit.Point))
            using (var brush = new SolidBrush(Color.FromArgb(112, 120, 138)))
                g.DrawString(muted ? "静音" : volume.ToString(CultureInfo.InvariantCulture), font, brush,
                    new PointF(volX1 + 8 * scale, volY - 7 * scale));

            int closeX = Width - pad - (int)Math.Round(2 * scale);
            int closeY = titleY + (int)Math.Round(8 * scale);
            using (var pen = new Pen(Color.FromArgb(120, 128, 146), 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, closeX - 4 * scale, closeY - 4 * scale, closeX + 4 * scale, closeY + 4 * scale);
                g.DrawLine(pen, closeX + 4 * scale, closeY - 4 * scale, closeX - 4 * scale, closeY + 4 * scale);
            }

            // 锁定：在关闭左边画一把小锁，锁上时高亮
            int lockX = closeX - (int)Math.Round(22 * scale);
            int lockY = closeY;
            Color lockColor = locked ? Color.FromArgb(196, 166, 255) : Color.FromArgb(120, 128, 146);
            using (var pen = new Pen(lockColor, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, lockX - 4 * scale, lockY - 8 * scale, 8 * scale, 9 * scale, 200, 140);
                using (var body = new SolidBrush(lockColor))
                    g.FillRectangle(body, lockX - 5 * scale, lockY - 3 * scale, 10 * scale, 8 * scale);
            }

            if (editingBackground)
            {
                Rectangle confirm = BackgroundConfirmRect();
                int hintH = (int)Math.Round(30 * scale);
                Rectangle hint = new Rectangle(pad, (int)Math.Round(titleY - 4 * scale),
                    Math.Max(0, confirm.Left - pad - (int)Math.Round(8 * scale)), hintH);
                using (var shade = new SolidBrush(Color.FromArgb(214, 14, 16, 21)))
                using (var hintPath = RoundedRect(hint, (int)Math.Round(10 * scale)))
                    g.FillPath(shade, hintPath);
                using (var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
                using (var brush = new SolidBrush(Color.FromArgb(232, 235, 242)))
                using (var fmt = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                    LineAlignment = StringAlignment.Center,
                })
                    g.DrawString("拖动背景，选好位置点确定", font, brush,
                        new RectangleF(hint.X + 10 * scale, hint.Y, hint.Width - 10 * scale, hint.Height), fmt);
                using (var fill = new SolidBrush(Color.FromArgb(143, 168, 255)))
                using (var confirmPath = RoundedRect(confirm, (int)Math.Round(13 * scale)))
                    g.FillPath(fill, confirmPath);
                using (var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point))
                using (var brush = new SolidBrush(Color.FromArgb(14, 16, 21)))
                using (var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                })
                    g.DrawString("确定", font, brush, confirm, fmt);
            }
        }

        static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, radius, radius, 180, 90);
            path.AddArc(rect.Right - radius, rect.Y, radius, radius, 270, 90);
            path.AddArc(rect.Right - radius, rect.Bottom - radius, radius, radius, 0, 90);
            path.AddArc(rect.X, rect.Bottom - radius, radius, radius, 90, 90);
            path.CloseAllFigures();
            return path;
        }

        static string PrettyApp(string app)
        {
            string n = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app.Substring(0, app.Length - 4) : app;
            switch (n.ToLowerInvariant())
            {
                case "chrome": return "Chrome";
                case "msedge": return "Edge";
                case "firefox": return "Firefox";
                case "brave": return "Brave";
                case "vivaldi": return "Vivaldi";
                case "qqbrowser": return "QQ 浏览器";
                default: return n.Length == 0 ? "输出" : char.ToUpperInvariant(n[0]) + n.Substring(1);
            }
        }

        int HitTest(Point p)
        {
            int pad = (int)Math.Round(16 * scale);
            int playR = (int)Math.Round(11 * scale);
            int playX = pad + playR;
            int playY = Height - (int)Math.Round(34 * scale) + playR - (int)Math.Round(2 * scale);
            if ((p.X - playX) * (p.X - playX) + (p.Y - playY) * (p.Y - playY) <= (playR + 4) * (playR + 4)) return 1;
            if (LockRect().Contains(p)) return 4;
            int closeX = Width - pad - (int)Math.Round(2 * scale);
            int closeY = (int)Math.Round(20 * scale);
            if (Math.Abs(p.X - closeX) <= 12 * scale && Math.Abs(p.Y - closeY) <= 12 * scale) return 2;
            int speakerX = pad + playR * 2 + (int)Math.Round(14 * scale);
            int volX0 = speakerX + (int)Math.Round(16 * scale);
            int volX1 = Width - pad - (int)Math.Round(42 * scale);
            if (p.Y >= playY - 10 * scale && p.Y <= playY + 10 * scale && p.X >= volX0 - 6 * scale && p.X <= volX1 + 6 * scale) return 3;
            return 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (editingBackground)
            {
                if (BackgroundConfirmRect().Contains(e.Location))
                {
                    ConfirmBackgroundEdit();
                    return;
                }
                bgDragging = true;
                bgDragStart = e.Location;
                bgStartOffsetX = bgOffsetX;
                bgStartOffsetY = bgOffsetY;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }
            int hit = HitTest(e.Location);
            if (hit == 1)
            {
                emit("{\"type\":\"ui\",\"action\":\"toggle\"}");
                Program.TogglePlayback();
                return;
            }
            if (hit == 2)
            {
                Hide();
                SaveSettings();
                emit("{\"type\":\"ui\",\"action\":\"hide\"}");
                return;
            }
            if (hit == 4)
            {
                ToggleLock();
                return;
            }
            if (hit == 3)
            {
                volumeDragging = true;
                SetVolumeFromX(e.X);
                return;
            }
            int edges = EdgeAt(e.Location);
            if (edges != 0 && !locked)
            {
                resizing = true;
                resizeEdges = edges;
                resizeStart = e.Location;
                resizeFrom = Bounds;
                Capture = true;
                return;
            }
            dragging = true;
            dragOrigin = new Point(e.X, e.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (editingBackground)
            {
                if (bgDragging)
                {
                    bgOffsetX = bgStartOffsetX + (e.X - bgDragStart.X);
                    bgOffsetY = bgStartOffsetY + (e.Y - bgDragStart.Y);
                    ClampBackgroundOffset();
                    Invalidate();
                }
                else
                {
                    Cursor = BackgroundConfirmRect().Contains(e.Location) ? Cursors.Hand : Cursors.SizeAll;
                }
                return;
            }
            if (resizing)
            {
                ResizeTo(e.Location);
                return;
            }
            if (volumeDragging)
            {
                SetVolumeFromX(e.X);
                return;
            }
            if (!dragging)
            {
                UpdateResizeCursor(e.Location);
                return;
            }
            Location = new Point(Location.X + e.X - dragOrigin.X, Location.Y + e.Y - dragOrigin.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (editingBackground)
            {
                if (bgDragging)
                {
                    bgDragging = false;
                    Capture = false;
                    ClampBackgroundOffset();
                    SaveSettings();
                }
                return;
            }
            if (resizing)
            {
                resizing = false;
                Capture = false;
                Cursor = Cursors.Default;
                SaveSettings();
                emit(SettingsJson());
                return;
            }
            if (volumeDragging)
            {
                volumeDragging = false;
                SaveSettings();
                return;
            }
            if (dragging)
            {
                dragging = false;
                SaveSettings();
            }
        }

        int EdgeAt(Point p)
        {
            int grip = Math.Max(5, (int)Math.Round(7 * scale));
            int edges = 0;
            if (p.X <= grip) edges |= 1;
            if (p.X >= Width - grip) edges |= 2;
            if (p.Y <= grip) edges |= 4;
            if (p.Y >= Height - grip) edges |= 8;
            return edges;
        }

        void UpdateResizeCursor(Point p)
        {
            int edges = EdgeAt(p);
            if (edges == 1 || edges == 2) Cursor = Cursors.SizeWE;
            else if (edges == 4 || edges == 8) Cursor = Cursors.SizeNS;
            else if (((edges & 1) != 0 && (edges & 4) != 0) || ((edges & 2) != 0 && (edges & 8) != 0)) Cursor = Cursors.SizeNWSE;
            else if (((edges & 2) != 0 && (edges & 4) != 0) || ((edges & 1) != 0 && (edges & 8) != 0)) Cursor = Cursors.SizeNESW;
            else Cursor = Cursors.Default;
        }

        void ResizeTo(Point p)
        {
            int dx = p.X - resizeStart.X;
            int dy = p.Y - resizeStart.Y;
            int x = resizeFrom.X, y = resizeFrom.Y, w = resizeFrom.Width, h = resizeFrom.Height;
            if ((resizeEdges & 1) != 0) { x += dx; w -= dx; }
            if ((resizeEdges & 2) != 0) w += dx;
            if ((resizeEdges & 4) != 0) { y += dy; h -= dy; }
            if ((resizeEdges & 8) != 0) h += dy;
            w = Math.Max((int)Math.Round(BASE_W * scale), Math.Min(1200, w));
            h = Math.Max((int)Math.Round(BASE_H * scale), Math.Min(600, h));
            if ((resizeEdges & 1) != 0) x = resizeFrom.Right - w;
            if ((resizeEdges & 4) != 0) y = resizeFrom.Bottom - h;
            Bounds = new Rectangle(x, y, w, h);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (editingBackground) return;
            Program.NudgeVolume(e.Delta > 0 ? 4 : -4);
        }

        void SetVolumeFromX(int x)
        {
            int pad = (int)Math.Round(16 * scale);
            int playR = (int)Math.Round(11 * scale);
            int speakerX = pad + playR * 2 + (int)Math.Round(14 * scale);
            int volX0 = speakerX + (int)Math.Round(16 * scale);
            int volX1 = Width - pad - (int)Math.Round(42 * scale);
            float t = (x - volX0) / (float)(volX1 - volX0);
            t = Math.Max(0, Math.Min(1, t));
            Program.SetVolumePercent((int)Math.Round(t * 100));
            Invalidate();
        }
    }
}
