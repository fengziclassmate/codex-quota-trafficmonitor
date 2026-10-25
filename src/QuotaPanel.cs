using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

public static partial class Bridge
{
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int awareness);

    static int Panel(int hostId, IntPtr anchor, bool pinned)
    {
        SetProcessDpiAwareness(2);
        int result = 0;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) {
            result = 1;
            System.IO.File.WriteAllText(System.IO.Path.Combine(DataDir, "panel-error.log"), DateTimeOffset.Now.ToString("O") + Environment.NewLine + e.Exception.ToString());
            Application.ExitThread();
        };
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var panel = new QuotaPanel(hostId, anchor, pinned)) Application.Run();
        return result;
    }

    public sealed class QuotaPanel : Form
    {
        public sealed class Cycle { public string remaining, cycle; public double percent; public long? reset; }
        public sealed class TaskItem { public string title, phase; }
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetPrivateProfileString(string section, string key, string fallback, StringBuilder result, uint size, string file);

        readonly Process host;
        readonly IntPtr anchor;
        readonly System.Windows.Forms.Timer timer;
        readonly Label title, state, creditsLabel, creditsCount, expiryLabel, expiryValue, tasksLabel, taskCount, updated, next;
        readonly Label[] cycleLabels = new Label[2], numbers = new Label[2], suffixes = new Label[2], resetLabels = new Label[2], resetDates = new Label[2];
        readonly Label[] taskTitles = new Label[3], taskStates = new Label[3];
        readonly Button close;
        readonly List<float> separators = new List<float>();
        readonly List<RectangleF> tracks = new List<RectangleF>();
        readonly List<double> percentages = new List<double>();
        Font body, heading, number;
        float scale;
        bool pinned, darkMode, themeReady;
        long hoverSince, leaveSince;
        int ticks;
        string signature;
        Color background, subtle, hovered, line, text, muted, accent, accentFill, warning;

        public QuotaPanel(int hostId, IntPtr anchorWindow, bool initiallyPinned)
        {
            host = Process.GetProcessById(hostId); anchor = anchorWindow; pinned = initiallyPinned;
            scale = GetDpiForWindow(anchor) / 96f;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; TopMost = true; DoubleBuffered = true; AutoScaleMode = AutoScaleMode.None;
            Text = "CodexQuota.Panel." + hostId; AccessibleName = "Codex 额度与任务状态"; KeyPreview = true;
            SetFonts();
            title = AddLabel(); state = AddLabel();
            for (int i = 0; i < 2; i++) { cycleLabels[i] = AddLabel(); numbers[i] = AddLabel(); suffixes[i] = AddLabel(); resetLabels[i] = AddLabel(); resetDates[i] = AddLabel(); }
            creditsLabel = AddLabel(); creditsCount = AddLabel(); expiryLabel = AddLabel(); expiryValue = AddLabel();
            tasksLabel = AddLabel(); taskCount = AddLabel(); updated = AddLabel(); next = AddLabel();
            for (int i = 0; i < 3; i++) { taskTitles[i] = AddLabel(); taskStates[i] = AddLabel(); }
            close = new Button { Text = "关闭", AccessibleName = "关闭详情", FlatStyle = FlatStyle.Flat, TabStop = true, Cursor = Cursors.Hand };
            close.FlatAppearance.BorderSize = 0; close.Click += delegate { Dismiss(); }; Controls.Add(close); CancelButton = close;
            Deactivate += delegate { if (pinned) Dismiss(); };
            FormClosed += delegate { Application.ExitThread(); };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Dismiss(); e.Handled = true; } };
            var handle = Handle;
            ReadTheme(); UpdateView();
            timer = new System.Windows.Forms.Timer { Interval = 125 };
            timer.Tick += Tick; timer.Start();
            if (pinned) Reveal();
        }

        protected override bool ShowWithoutActivation { get { return !pinned; } }
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x80; return value; } }

        Label AddLabel()
        {
            var label = new Label { AutoSize = false, AutoEllipsis = true, UseMnemonic = false, AccessibleRole = AccessibleRole.StaticText };
            Controls.Add(label); return label;
        }

        void SetFonts()
        {
            // Detach shared fonts before disposal; equal replacement fonts may be ignored by WinForms.
            foreach (Control control in Controls) control.Font = null;
            if (body != null) { body.Dispose(); heading.Dispose(); number.Dispose(); }
            body = new Font("Microsoft YaHei UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            heading = new Font("Microsoft YaHei UI", 16 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            number = new Font("Segoe UI", 28 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        static Color Hex(string value) { return ColorTranslator.FromHtml(value); }
        void ApplyTheme(bool dark)
        {
            darkMode = dark; themeReady = true;
            // Radix Colors: gray surfaces/borders, step 12 text on light surfaces.
            background = Hex(dark ? "#191919" : "#f9f9f9"); subtle = Hex(dark ? "#222222" : "#f0f0f0");
            hovered = Hex(dark ? "#2a2a2a" : "#e8e8e8"); line = Hex(dark ? "#3a3a3a" : "#d9d9d9");
            text = Hex(dark ? "#eeeeee" : "#202020"); muted = Hex(dark ? "#b4b4b4" : "#646464");
            accent = Hex(dark ? "#0bd8b6" : "#0d3d38"); accentFill = Hex("#12a594"); warning = Hex(dark ? "#ffca16" : "#4f3422");
            BackColor = background;
            close.BackColor = background; close.ForeColor = muted;
            close.FlatAppearance.MouseOverBackColor = subtle; close.FlatAppearance.MouseDownBackColor = hovered;
        }

        string Read(string file, string key, string fallback = "")
        {
            var result = new StringBuilder(32768);
            GetPrivateProfileString("data", key, fallback, result, (uint)result.Capacity, System.IO.Path.Combine(DataDir, file + ".ini"));
            return result.ToString();
        }

        void LabelAt(Label label, string value, float x, float y, float width, float height, Font font, Color color, bool right = false)
        {
            label.Visible = true; label.Text = value; label.Font = font; label.ForeColor = color; label.BackColor = background;
            label.TextAlign = right ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
            label.SetBounds((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale));
        }

        void ReadTheme()
        {
            bool dark = Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1)) == 0;
            if (!themeReady || darkMode != dark) { ApplyTheme(dark); signature = null; }
        }

        void UpdateView()
        {
            string cyclesJson = Read("quota", "cycles_json", "[]"), tasksJson = Read("activity", "items_json", "[]");
            string status = Read("activity", "label", "读取中"), remaining = Read("quota", "remaining", "--");
            long statusTime = long.Parse(Read("activity", "updated", "0"), CultureInfo.InvariantCulture);
            if (UnixNow > statusTime + 10) { status = "状态不可用"; tasksJson = "[]"; }
            string credit = Read("quota", "reset_count", "未知"), expires = Read("quota", "reset_expiries");
            long quotaTime = long.Parse(Read("quota", "updated", "0"), CultureInfo.InvariantCulture);
            bool stale = UnixNow > quotaTime + 300 || Read("quota", "error") != "";
            string current = string.Join("|", new[] { cyclesJson, tasksJson, status, remaining, credit, expires, quotaTime.ToString(), stale.ToString(), pinned.ToString(), scale.ToString(CultureInfo.InvariantCulture) });
            if (current == signature) return;
            signature = current;
            var cycles = Json.Deserialize<Cycle[]>(cyclesJson);
            var tasks = Json.Deserialize<TaskItem[]>(tasksJson);
            SuspendLayout(); separators.Clear(); tracks.Clear(); percentages.Clear();
            foreach (Control control in Controls) control.Visible = false;
            LabelAt(title, "Codex", 20, 16, 100, 28, heading, text);
            Color statusColor = status.Contains("等待") || status.Contains("批准") || status.Contains("不可用") ? warning : status == "空闲" ? muted : accent;
            LabelAt(state, status, 126, 16, pinned ? 142 : 198, 28, body, statusColor, true);
            close.Visible = pinned; close.Font = body; close.SetBounds((int)(280 * scale), (int)(14 * scale), (int)(48 * scale), (int)(32 * scale));
            float y = 62;
            int cycleCount = Math.Max(1, cycles.Length);
            for (int i = 0; i < cycleCount; i++)
            {
                var cycle = cycles.Length == 0 ? new Cycle { remaining = remaining, cycle = "剩余额度" } : cycles[i];
                LabelAt(cycleLabels[i], cycle.cycle.Replace("周期", "额度"), 20, y, 304, 20, body, muted);
                int width = TextRenderer.MeasureText(cycle.remaining, number).Width;
                LabelAt(numbers[i], cycle.remaining, 18, y + 20, width / scale + 2, 44, number, stale ? muted : text);
                if (cycles.Length > 0) {
                    LabelAt(suffixes[i], "剩余", 18 + width / scale + 8, y + 36, 48, 22, body, muted);
                    tracks.Add(new RectangleF(20 * scale, (y + 74) * scale, 304 * scale, 3 * scale)); percentages.Add(cycle.percent);
                }
                LabelAt(resetLabels[i], "额度重置", 20, y + 88, 90, 20, body, muted);
                LabelAt(resetDates[i], cycle.reset.HasValue ? DateTimeOffset.FromUnixTimeSeconds(cycle.reset.Value).ToLocalTime().ToString("MM/dd HH:mm") : "未知", 120, y + 88, 204, 20, body, text, true);
                y += 128;
            }
            separators.Add(y * scale); y += 16;
            LabelAt(creditsLabel, "可用重置", 20, y, 120, 26, body, muted);
            LabelAt(creditsCount, credit == "未知" ? credit : credit + " 次", 170, y, 154, 26, heading, text, true);
            string[] expiryDates = expires.Split(new[] { '\u2028' }, StringSplitOptions.RemoveEmptyEntries);
            LabelAt(expiryLabel, expiryDates.Length > 1 ? "最早到期" : "到期时间", 20, y + 32, 90, 20, body, muted);
            LabelAt(expiryValue, expiryDates.Length == 0 ? "未知" : expiryDates.OrderBy(v => v, StringComparer.Ordinal).First().Replace('-', '/'), 120, y + 32, 204, 20, body, text, true);
            y += 72;
            if (tasks.Length > 0)
            {
                separators.Add(y * scale); y += 16;
                LabelAt(tasksLabel, "活动任务", 20, y, 130, 22, body, muted);
                LabelAt(taskCount, Read("activity", "active", "0") + " 项", 240, y, 84, 22, body, muted, true); y += 30;
                for (int i = 0; i < tasks.Length; i++)
                {
                    string phase = tasks[i].phase == "running" ? "运行" : tasks[i].phase == "thinking" ? "思考" : tasks[i].phase == "waiting_approval" ? "批准/执行" : "等待交互";
                    LabelAt(taskTitles[i], tasks[i].title, 20, y, 220, 22, body, text);
                    LabelAt(taskStates[i], phase, 246, y, 78, 22, body, phase.StartsWith("等待") || phase.StartsWith("批准") ? warning : accent, true); y += 28;
                }
                y += 8;
            }
            separators.Add(y * scale); y += 14;
            string stamp = quotaTime == 0 ? "尚未更新" : "更新 " + DateTimeOffset.FromUnixTimeSeconds(quotaTime).ToLocalTime().ToString("MM/dd HH:mm");
            LabelAt(updated, stamp, 20, y, 180, 20, body, muted);
            LabelAt(next, stale ? "等待刷新" : "下次 " + DateTimeOffset.FromUnixTimeSeconds(quotaTime + 120).ToLocalTime().ToString("HH:mm"), 202, y, 122, 20, body, stale ? warning : muted, true);
            ClientSize = new Size((int)(344 * scale), (int)((y + 36) * scale));
            ResumeLayout(false); Invalidate();
            if (Visible) PositionPanel();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var graphics = e.Graphics; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(line, .5f * scale)) {
                graphics.DrawRectangle(pen, .25f * scale, .25f * scale, ClientSize.Width - .5f * scale, ClientSize.Height - .5f * scale);
                foreach (float y in separators) graphics.DrawLine(pen, 20 * scale, y, 324 * scale, y);
            }
            using (var track = new SolidBrush(subtle)) using (var fill = new SolidBrush(accentFill))
                for (int i = 0; i < tracks.Count; i++) { graphics.FillRectangle(track, tracks[i]); var area = tracks[i]; area.Width *= (float)(percentages[i] / 100); graphics.FillRectangle(fill, area); }
        }

        void PositionPanel()
        {
            NativeRect rect; GetWindowRect(anchor, out rect);
            Rectangle work = Screen.FromHandle(anchor).WorkingArea;
            int left = Math.Max(work.Left + 8, Math.Min(rect.Left, work.Right - Width - 8));
            int top = rect.Top - Height - (int)(8 * scale);
            if (top < work.Top) top = Math.Min(work.Bottom - Height - 8, rect.Bottom + (int)(8 * scale));
            Location = new Point(left, Math.Max(work.Top + 8, top));
        }

        void Tick(object sender, EventArgs e)
        {
            if (host.HasExited || !IsWindow(anchor)) { timer.Stop(); Application.ExitThread(); return; }
            float dpi = GetDpiForWindow(anchor) / 96f;
            if (dpi != scale) { scale = dpi; SetFonts(); signature = null; UpdateView(); }
            if (++ticks % 4 == 0) { ReadTheme(); UpdateView(); }
            if (pinned) return;
            NativeRect rect; GetWindowRect(anchor, out rect);
            var point = Cursor.Position;
            bool overAnchor = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom).Contains(point);
            var panelArea = Bounds; panelArea.Inflate(10, 10);
            if (overAnchor || Visible && panelArea.Contains(point))
            {
                leaveSince = 0;
                if (hoverSince == 0) hoverSince = Environment.TickCount;
                if (!Visible && Environment.TickCount - hoverSince >= 350) { UpdateView(); Reveal(); }
            }
            else
            {
                hoverSince = 0;
                if (leaveSince == 0) leaveSince = Environment.TickCount;
                if (Visible && Environment.TickCount - leaveSince > 200) Hide();
            }
        }

        void Dismiss() { pinned = false; Hide(); hoverSince = 0; signature = null; }
        void Reveal()
        {
            PositionPanel(); Show();
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
            if (pinned) { Activate(); close.Focus(); }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x10) { Dismiss(); return; }
            if (message.Msg == 0x8000 + 91) { pinned = true; UpdateView(); Reveal(); return; }
            base.WndProc(ref message);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (timer != null) timer.Dispose(); body.Dispose(); heading.Dispose(); number.Dispose(); host.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
