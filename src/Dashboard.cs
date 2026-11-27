using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AnbyPet
{
    internal sealed class PanelData
    {
        internal double? Cpu, GpuLoad, Fps;
        internal RunningProviders Providers = new RunningProviders();
        internal QuotaSnapshot Codex, Antigravity;
        internal string CodexStatus;
        internal string AntigravityStatus;
        internal string StorageError;
    }

    internal sealed class Dashboard : Form
    {
        private const int CaptionHeight = 32;
        private const int MaximumBoardHeight = 510;
        private const int ScrollBarWidth = 16;
        private const int ScrollLineHeight = 24;
        private readonly Panel viewport;
        private readonly Board board;
        private readonly DashboardScrollBar scrollBar;
        private readonly Button close;
        private readonly Button applications;
        private readonly Font closeFont = new Font("Segoe UI", 13);
        private Rectangle? returnBounds;
        private bool dragging;
        private int wheelRemainder;

        internal Dashboard(Icon icon, ContextMenuStrip applicationMenu)
        {
            Text = "安比桌宠";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Board.Canvas;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            ClientSize = new Size(420, MaximumBoardHeight + CaptionHeight + 2);
            Padding = new Padding(1, CaptionHeight + 1, 1, 1);
            viewport = new Panel { Dock = DockStyle.Fill, BackColor = Board.Canvas };
            board = new Board(icon);
            scrollBar = new DashboardScrollBar();
            viewport.Controls.Add(board);
            viewport.Controls.Add(scrollBar);
            scrollBar.BringToFront();
            Controls.Add(viewport);
            close = new Button
            {
                Text = "×",
                AccessibleName = "关闭监测面板",
                Font = closeFont,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                ForeColor = Board.Muted,
                BackColor = Board.Canvas,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Bounds = new Rectangle(ClientSize.Width - 38, 3, 34, 26)
            };
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = Board.Divider;
            close.FlatAppearance.MouseDownBackColor = Board.Surface;
            close.Click += delegate { Hide(); };
            Controls.Add(close);
            applications = new Button
            {
                Text = "应用管理",
                Font = board.LabelFont,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                ForeColor = Board.Muted,
                BackColor = Board.Canvas,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Bounds = new Rectangle(close.Left - 78, 4, 74, 24)
            };
            applications.FlatAppearance.BorderColor = Board.Divider;
            applications.FlatAppearance.MouseOverBackColor = Board.Divider;
            applications.FlatAppearance.MouseDownBackColor = Board.Surface;
            applications.Click += delegate
            {
                applicationMenu.Show(applications, new Point(applications.Width, applications.Height),
                    ToolStripDropDownDirection.BelowLeft);
            };
            Controls.Add(applications);
            viewport.Resize += delegate { FitBoard(); };
            scrollBar.ValueChanged += delegate { board.Top = -scrollBar.Value; };
        }

        internal void Open(PanelData data)
        {
            UpdateReadings(data);
            if (!Visible)
            {
                var area = returnBounds.HasValue ? Screen.FromControl(this).WorkingArea : Screen.FromPoint(Cursor.Position).WorkingArea;
                Height = PanelHeight(area);
                if (!returnBounds.HasValue)
                    Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
                Show();
            }
            Activate();
        }

        internal void UpdateReadings(PanelData data)
        {
            int previousHeight = board.Height;
            board.UpdateReadings(data);
            if (Visible && board.Height != previousHeight)
            {
                var area = Screen.FromControl(this).WorkingArea;
                int height = PanelHeight(area);
                int top = Math.Max(area.Top, Math.Min(Bottom, area.Bottom) - height);
                SetBounds(Left, top, Width, height);
            }
            FitBoard();
        }

        private int PanelHeight(Rectangle area)
        {
            return Math.Min(Padding.Vertical + Math.Min(board.Height, MaximumBoardHeight), Math.Max(180, area.Height - 24));
        }

        internal void PlaceForFullscreen(PetDisplays.Display secondary)
        {
            if (!Visible || dragging) return;
            var bounds = PetDisplays.WindowBounds(Handle);
            if (bounds.IsEmpty) return;
            if (secondary.Handle != IntPtr.Zero)
            {
                if (secondary.WorkArea.Contains(bounds)) return;
                if (MoveToDisplay(secondary, bounds, null) && !returnBounds.HasValue) returnBounds = bounds;
            }
            else if (returnBounds.HasValue)
            {
                var previous = returnBounds.Value;
                var display = PetDisplays.ForBounds(previous);
                if (display.Handle != IntPtr.Zero && MoveToDisplay(display, bounds, previous.Location))
                    returnBounds = null;
            }
        }

        private bool MoveToDisplay(PetDisplays.Display display, Rectangle current, Point? restore)
        {
            var source = PetDisplays.ForBounds(current);
            var size = current.Size;
            double scale = display.Scale();
            if (source.Handle != IntPtr.Zero && source.Handle != display.Handle)
            {
                // Windows rescales this panel when it crosses displays. Measure its new bounds after the move.
                double ratio = scale / source.Scale();
                size = new Size((int)Math.Round(size.Width * ratio), (int)Math.Round(size.Height * ratio));
            }
            int inset = (int)Math.Round(16 * scale, MidpointRounding.AwayFromZero);
            var work = display.WorkArea;
            var point = Placement(work, size, inset, restore);
            if (point != current.Location && !PetDisplays.MoveWindow(Handle, point)) return false;
            var placed = PetDisplays.WindowBounds(Handle);
            if (placed.IsEmpty) return false;
            var adjusted = Placement(work, placed.Size, inset, restore);
            return adjusted == placed.Location || PetDisplays.MoveWindow(Handle, adjusted);
        }

        private static Point Placement(Rectangle work, Size size, int inset, Point? restore)
        {
            return restore.HasValue
                ? PetDisplays.Fit(work, restore.Value, size).Location
                : PetDisplays.BottomLeft(work, size, inset, Rectangle.Empty).Location;
        }

        private void FitBoard()
        {
            int height = viewport.ClientSize.Height;
            int width = viewport.ClientSize.Width;
            bool overflow = board.Height > height;
            int railWidth = overflow ? Math.Min(ScrollBarWidth, width) : 0;
            board.Width = Math.Max(1, width - railWidth);
            scrollBar.SetBounds(width - railWidth, 0, railWidth, height);
            scrollBar.SetRange(board.Height, height);
            scrollBar.Visible = overflow;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Board.Divider))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                e.Graphics.DrawLine(pen, 1, Padding.Top - 1, ClientSize.Width - 2, Padding.Top - 1);
            }
            Board.Draw(e.Graphics, Text, board.LabelFont, Board.Muted,
                new Rectangle(16, 1, applications.Left - 20, Padding.Top - 1));
        }

        protected override void WndProc(ref Message message)
        {
            const int hitTest = 0x0084;
            const int client = 1;
            const int caption = 2;
            if (message.Msg == 0x0231) dragging = true;
            if (message.Msg == 0x0232) dragging = false;
            base.WndProc(ref message);
            if (message.Msg != hitTest || message.Result != (IntPtr)client || applications == null) return;
            long position = message.LParam.ToInt64();
            var point = PointToClient(new Point(unchecked((short)position), unchecked((short)(position >> 16))));
            if (point.Y >= 0 && point.Y < Padding.Top && point.X >= 0 && point.X < applications.Left)
                message.Result = (IntPtr)caption;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            if (scrollBar.Maximum > 0 && lines != 0)
            {
                const int deltaPerNotch = 120;
                int step = lines < 0 ? viewport.ClientSize.Height : lines * ScrollLineHeight;
                int delta = e.Delta * step + wheelRemainder;
                wheelRemainder = delta % deltaPerNotch;
                scrollBar.ScrollTo(scrollBar.Value - delta / deltaPerNotch);
                var handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
            }
            base.OnMouseWheel(e);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (scrollBar.Maximum == 0) return base.ProcessCmdKey(ref message, keyData);
            int page = Math.Max(ScrollLineHeight, viewport.ClientSize.Height - ScrollLineHeight);
            switch (keyData)
            {
                case Keys.Up: scrollBar.ScrollTo(scrollBar.Value - ScrollLineHeight); break;
                case Keys.Down: scrollBar.ScrollTo(scrollBar.Value + ScrollLineHeight); break;
                case Keys.PageUp: scrollBar.ScrollTo(scrollBar.Value - page); break;
                case Keys.PageDown: scrollBar.ScrollTo(scrollBar.Value + page); break;
                case Keys.Home: scrollBar.ScrollTo(0); break;
                case Keys.End: scrollBar.ScrollTo(scrollBar.Maximum); break;
                default: return base.ProcessCmdKey(ref message, keyData);
            }
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // WinForms classifies a direct WM_CLOSE as TaskManagerClosing.
            if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) closeFont.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class DashboardScrollBar : Control
    {
        private const int TrackInset = 8;
        private const int MinimumThumbHeight = 24;
        private static readonly Color ThumbColor = Color.FromArgb(95, 114, 101);
        private int contentHeight;
        private int viewportHeight;
        private int dragOffset = -1;

        internal int Value { get; private set; }
        internal int Maximum { get { return Math.Max(0, contentHeight - viewportHeight); } }
        internal event EventHandler ValueChanged;

        internal DashboardScrollBar()
        {
            BackColor = Board.Canvas;
            TabStop = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }

        internal void SetRange(int content, int viewport)
        {
            if (content == contentHeight && viewport == viewportHeight) return;
            contentHeight = content;
            viewportHeight = viewport;
            ScrollTo(Value);
            Invalidate();
        }

        internal void ScrollTo(int position)
        {
            int next = Math.Max(0, Math.Min(Maximum, position));
            if (next == Value) return;
            Value = next;
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            Invalidate();
        }

        private int TrackHeight { get { return Math.Max(0, Height - 2 * TrackInset); } }

        private Rectangle ThumbBounds()
        {
            if (Maximum == 0 || TrackHeight == 0) return Rectangle.Empty;
            int length = Math.Max(MinimumThumbHeight, (int)Math.Round(TrackHeight * (double)viewportHeight / contentHeight));
            int height = Math.Min(TrackHeight, length);
            int top = TrackInset + (int)Math.Round((TrackHeight - height) * (double)Value / Maximum);
            return new Rectangle(0, top, Width, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var thumb = ThumbBounds();
            if (thumb.IsEmpty || Width < 5) return;
            bool active = dragOffset >= 0 || ClientRectangle.Contains(PointToClient(Cursor.Position));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            FillPill(e.Graphics, (Width - 5) / 2, TrackInset, 5, TrackHeight, Board.Surface);
            FillPill(e.Graphics, (Width - 5) / 2, thumb.Top, 5, thumb.Height, active ? Board.Muted : ThumbColor);
        }

        private static void FillPill(Graphics graphics, int x, int y, int width, int height, Color color)
        {
            int diameter = Math.Min(width, height);
            using (var brush = new SolidBrush(color))
            {
                graphics.FillEllipse(brush, x, y, width, diameter);
                graphics.FillRectangle(brush, x, y + diameter / 2, width, height - diameter + 1);
                graphics.FillEllipse(brush, x, y + height - diameter, width, diameter);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Maximum == 0) return;
            var thumb = ThumbBounds();
            if (thumb.Contains(e.Location))
            {
                dragOffset = e.Y - thumb.Top;
                Capture = true;
                Invalidate();
            }
            else
            {
                ScrollTo(Value + (e.Y < thumb.Top ? -viewportHeight : viewportHeight));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragOffset < 0) return;
            int travel = TrackHeight - ThumbBounds().Height;
            if (travel > 0)
                ScrollTo((int)Math.Round((e.Y - dragOffset - TrackInset) * (double)Maximum / travel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && dragOffset >= 0) Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture) return;
            dragOffset = -1;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Invalidate();
        }
    }

    internal sealed class Board : Control
    {
        internal static readonly Color Canvas = Color.FromArgb(21, 26, 24);
        internal static readonly Color Surface = Color.FromArgb(34, 41, 37);
        private static readonly Color Ink = Color.FromArgb(244, 248, 242);
        internal static readonly Color Muted = Color.FromArgb(168, 185, 171);
        private static readonly Color Lime = Color.FromArgb(211, 250, 111);
        private static readonly Color Amber = Color.FromArgb(255, 204, 129);
        internal static readonly Color Divider = Color.FromArgb(54, 65, 57);
        private const TextFormatFlags CellFlags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        private readonly Font title = new Font("Microsoft YaHei UI", 16, FontStyle.Bold);
        private readonly Font heading = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        private readonly Font number = new Font("Segoe UI", 18, FontStyle.Bold);
        private readonly Font small = new Font("Microsoft YaHei UI", 9);
        private readonly Icon idleAnby;
        private PanelData data = new PanelData();

        internal Font LabelFont { get { return small; } }

        internal Board(Icon icon)
        {
            idleAnby = new Icon(icon, 48, 48);
            BackColor = Canvas;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }

        internal static string Percentage(double? value)
        {
            return value.HasValue ? value.Value.ToString("0", CultureInfo.CurrentCulture) + "%" : "—";
        }

        internal static string FrameRate(double? value)
        {
            return value.HasValue ? value.Value.ToString("0", CultureInfo.CurrentCulture) + " FPS" : "—";
        }

        internal static string QuotaTime(DateTimeOffset time)
        {
            var local = time.LocalDateTime;
            return local.ToString(local.Date == DateTime.Today ? "HH:mm" : "MM/dd HH:mm");
        }

        internal void UpdateReadings(PanelData readings)
        {
            data = readings;
            int codexRows = data.Codex == null ? 0 : data.Codex.Windows.Count;
            int antigravityRows = data.Antigravity == null ? 0 : data.Antigravity.Windows.Count;
            if (data.Providers.Codex && data.Codex != null && !string.IsNullOrWhiteSpace(data.CodexStatus))
                codexRows++;
            if (data.Providers.Antigravity && data.Antigravity != null && !string.IsNullOrWhiteSpace(data.AntigravityStatus))
                antigravityRows++;
            Height = 240 + 24 * (codexRows + antigravityRows) +
                (data.StorageError == null ? 0 : 33);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int left = 16, width = ClientSize.Width - 2 * left;
            var header = new Rectangle(left, 12, width, 33);
            Draw(g, "系统监测", title, Ink, header);
            Draw(g, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), title, Muted, header, true);

            int cardWidth = (width - 16) / 3;
            Metric(g, left, 55, cardWidth, "CPU", Percentage(data.Cpu), data.Cpu);
            Metric(g, left + cardWidth + 8, 55, cardWidth, "GPU", Percentage(data.GpuLoad), data.GpuLoad);
            Metric(g, left + 2 * (cardWidth + 8), 55, width - 2 * (cardWidth + 8), "游戏帧率",
                FrameRate(data.Fps), null, data.Fps.HasValue ? null : idleAnby);

            int y = 149;
            Section(g, "AI 额度", left, width, ref y);
            Provider(g, "Codex", data.Providers.Codex, "会话快照", data.CodexStatus, data.Codex, left, width, ref y);
            Provider(g, "Antigravity", data.Providers.Antigravity, "CLI 同步", data.AntigravityStatus, data.Antigravity, left, width, ref y);
            if (data.StorageError != null)
                Draw(g, data.StorageError, small, Amber, new Rectangle(left, y + 9, width, 24));
        }

        private void Metric(Graphics g, int x, int y, int w, string label, string value, double? progress, Icon portrait = null)
        {
            using (var fill = new SolidBrush(Surface)) g.FillRectangle(fill, x, y, w, 76);
            Draw(g, label, small, Muted, new Rectangle(x + 9, y + 8, w - 18, 19));
            if (portrait == null)
                Draw(g, value, number, Ink, new Rectangle(x + 9, y + 24, w - 18, 36));
            else
                g.DrawIcon(portrait, new Rectangle(x + 9, y + 27, 42, 42));
            if (progress.HasValue)
            {
                using (var track = new SolidBrush(Divider)) g.FillRectangle(track, x + 10, y + 64, w - 20, 3);
                using (var active = new SolidBrush(Lime)) g.FillRectangle(active, x + 10, y + 64,
                    (int)Math.Round((w - 20) * progress.Value / 100.0), 3);
            }
        }

        private void Section(Graphics g, string label, int left, int width, ref int y)
        {
            Draw(g, label, heading, Ink, new Rectangle(left, y, width, 23));
            y += 26;
            using (var pen = new Pen(Divider)) g.DrawLine(pen, left, y, left + width - 1, y);
            y += 3;
        }

        private void Row(Graphics g, string label, string value, int left, int width, ref int y, Color? valueColor = null)
        {
            int rightWidth = Math.Min(width / 2 + 45, TextRenderer.MeasureText(g, value, small).Width + 4);
            Draw(g, label, small, Muted, new Rectangle(left, y, width - rightWidth - 5, 24));
            Draw(g, value, small, valueColor ?? Ink, new Rectangle(left + width - rightWidth, y, rightWidth, 24), true);
            y += 24;
        }

        private void Provider(Graphics g, string name, bool active, string source, string pendingStatus, QuotaSnapshot snapshot,
            int left, int width, ref int y)
        {
            string labelSource = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Source) ? snapshot.Source : source;
            string waiting = string.IsNullOrWhiteSpace(pendingStatus) ? "等待额度" : pendingStatus;
            string status = !active ? "未运行" : snapshot == null ? waiting :
                labelSource + " · " + QuotaTime(snapshot.Captured);
            Row(g, name, status, left, width, ref y, active && snapshot == null ? (Color?)Amber : Muted);
            if (!active || snapshot == null) return;
            if (!string.IsNullOrWhiteSpace(pendingStatus))
                Row(g, "旧读数 · 最近查询失败", pendingStatus, left + 10, width - 10, ref y, Amber);
            foreach (var item in snapshot.Windows)
            {
                var now = DateTimeOffset.UtcNow;
                bool hasReset = item.Reset.HasValue && item.Reset.Value.Year > 1970;
                bool expired = hasReset && item.Reset.Value <= now;
                string value = expired ? "待更新" :
                    item.RemainingPercent.HasValue ? "剩 " + Percentage(item.RemainingPercent) :
                    "剩 " + item.RemainingAmount.GetValueOrDefault().ToString("0.##");
                if (hasReset && !expired)
                {
                    var reset = item.Reset.Value.LocalDateTime;
                    value += " · " + reset.ToString(reset.Date == DateTime.Today ? "HH:mm" : "MM/dd HH:mm") + " 重置";
                }
                Row(g, item.Name, value, left + 10, width - 10, ref y, Lime);
            }
        }

        internal static void Draw(Graphics g, string text, Font font, Color color, Rectangle rectangle, bool right = false)
        {
            TextRenderer.DrawText(g, text, font, rectangle, color, right ? CellFlags | TextFormatFlags.Right : CellFlags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                title.Dispose();
                heading.Dispose();
                number.Dispose();
                small.Dispose();
                idleAnby.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
