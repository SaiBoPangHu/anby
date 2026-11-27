using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AnbyPet
{
    internal sealed class DesktopPet : Form
    {
        private static readonly Size BaseSize = new Size(210, 258);
        private readonly Action showDetails;
        private readonly ContextMenuStrip quotaMenu = new ContextMenuStrip { ShowImageMargin = false };
        private Rectangle? returnBounds;
        private Rectangle destinationArea, destinationObstacle;
        private Point? mouseDown;
        private PetQuotaDisplay quotas;
        private string timeText = "", metricsText = "";
        private BurgerStage stage, cachedStage;
        private Bitmap cachedArt;
        private QuotaPopup quotaPopup;
        private bool quotaPinned, hoverDismissed;
        private bool dragging, drawingSprite;

        internal DesktopPet(Action showDetails)
        {
            this.showDetails = showDetails;
            Text = "安比";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = BaseSize;
            ContextMenuStrip = quotaMenu;
            quotaMenu.Opening += OpenQuotaMenu;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x80000 | 0x80 | 0x8000000;
                return parameters;
            }
        }

        internal void Open()
        {
            using (PetDisplays.PhysicalCoordinates.Enter())
            {
                if (!IsHandleCreated)
                {
                    var display = PetDisplays.AtCursor();
                    if (display.Handle != IntPtr.Zero) Bounds = Corner(display, false, Rectangle.Empty);
                }
                Show();
            }
        }

        internal void UpdateReadings(PanelData readings)
        {
            var now = DateTimeOffset.Now;
            var lastQuota = quotas;
            quotas = new PetQuotaDisplay(readings, now);
            if (quotaPopup != null && quotaPopup.Visible &&
                (lastQuota == null || !lastQuota.FiveHourRows.SequenceEqual(quotas.FiveHourRows)))
                ShowQuotaHint();

            var nextStage = PetArtwork.Stage(quotas.BurgerPercent);
            string time = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string fps = readings.Fps.HasValue ? Board.FrameRate(readings.Fps) : "FPS —";
            string metrics = "CPU " + Board.Percentage(readings.Cpu) + "   GPU " + Board.Percentage(readings.GpuLoad) + "   " + fps;
            bool changed = stage != nextStage || timeText != time || metricsText != metrics;
            stage = nextStage;
            timeText = time;
            metricsText = metrics;
            if (Visible && changed)
            {
                using (PetDisplays.PhysicalCoordinates.Enter())
                    DrawSprite(PetDisplays.WindowBounds(Handle));
            }
        }

        private void OpenQuotaMenu(object sender, CancelEventArgs e)
        {
            CloseQuotaHint();
            hoverDismissed = true;
            for (int index = quotaMenu.Items.Count - 1; index >= 0; index--)
            {
                var item = quotaMenu.Items[index];
                quotaMenu.Items.RemoveAt(index);
                item.Dispose();
            }
            quotaMenu.Items.Add(new ToolStripLabel("周额度"));
            if (quotas != null)
                foreach (var row in quotas.WeekRows) quotaMenu.Items.Add(new ToolStripLabel(row));
            quotaMenu.Items.Add(new ToolStripSeparator());
            quotaMenu.Items.Add("打开监测面板", null, delegate { showDetails(); });
            quotaMenu.Items.Add("隐藏安比", null, delegate { Hide(); });
            e.Cancel = false;
        }

        internal void PlaceForFullscreen(PetDisplays.Display secondary, Rectangle occupied)
        {
            if (!Visible || dragging) return;
            if (secondary.Handle != IntPtr.Zero)
            {
                if (returnBounds.HasValue && destinationArea == secondary.WorkArea && destinationObstacle == occupied) return;
                var previous = returnBounds ?? PetDisplays.WindowBounds(Handle);
                if (!previous.IsEmpty && DrawSprite(Corner(secondary, true, occupied)))
                {
                    returnBounds = previous;
                    destinationArea = secondary.WorkArea;
                    destinationObstacle = occupied;
                }
            }
            else if (returnBounds.HasValue)
            {
                var previous = returnBounds.Value;
                var display = PetDisplays.ForBounds(previous);
                if (display.Handle != IntPtr.Zero &&
                    DrawSprite(PetDisplays.Fit(display.WorkArea, previous.Location, SpriteSize(display.WorkArea, display.Scale()))))
                    returnBounds = null;
            }
        }

        private static Rectangle Corner(PetDisplays.Display display, bool left, Rectangle occupied)
        {
            var work = display.WorkArea;
            double scale = display.Scale();
            var size = SpriteSize(work, scale);
            int inset = Scaled(16, scale);
            if (left) return PetDisplays.BottomLeft(work, size, inset, occupied);
            var position = new Point(work.Right - size.Width - Scaled(26, scale), work.Bottom - size.Height - inset);
            return PetDisplays.Fit(work, position, size);
        }

        private static Size SpriteSize(Rectangle work, double scale)
        {
            scale = Math.Min(scale, Math.Min((double)work.Width / BaseSize.Width, (double)work.Height / BaseSize.Height));
            return new Size(Scaled(BaseSize.Width, scale), Scaled(BaseSize.Height, scale));
        }

        private static int Scaled(int value, double scale)
        {
            return Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DrawSprite(PetDisplays.WindowBounds(Handle));
        }

        private bool DrawSprite(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;
            drawingSprite = true;
            try
            {
                int edge = Math.Min(bounds.Width, bounds.Height);
                if (cachedArt == null || cachedArt.Width != edge || cachedStage != stage)
                {
                    var next = PetArtwork.Render(edge, stage);
                    if (cachedArt != null) cachedArt.Dispose();
                    cachedArt = next;
                    cachedStage = stage;
                }
                using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb))
                {
                    using (var paint = Graphics.FromImage(bitmap))
                    {
                        paint.DrawImageUnscaled(cachedArt, (bounds.Width - edge) / 2, 0);
                        DrawReadout(paint, bounds.Size, edge);
                    }
                    var source = Point.Empty;
                    var target = bounds.Location;
                    var dimensions = bitmap.Size;
                    var blend = new Blend { Op = 0, Alpha = 255, Format = 1 };
                    var windowDc = GetDC(IntPtr.Zero);
                    var memoryDc = CreateCompatibleDC(windowDc);
                    var surface = bitmap.GetHbitmap(Color.FromArgb(0));
                    var previous = SelectObject(memoryDc, surface);
                    try { return UpdateLayeredWindow(Handle, windowDc, ref target, ref dimensions, memoryDc, ref source, 0, ref blend, 2); }
                    finally
                    {
                        SelectObject(memoryDc, previous);
                        DeleteObject(surface);
                        DeleteDC(memoryDc);
                        ReleaseDC(IntPtr.Zero, windowDc);
                    }
                }
            }
            finally { drawingSprite = false; }
        }

        private void DrawReadout(Graphics paint, Size size, int edge)
        {
            if (size.Height <= edge) return;
            double scale = (double)edge / BaseSize.Width;
            float lineWidth = Math.Max(1f, (float)(2.6 * scale));
            float timeBand = (float)(26 * scale);
            paint.SmoothingMode = SmoothingMode.AntiAlias;
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var stroke = new Pen(Color.FromArgb(230, 24, 25, 29), lineWidth) { LineJoin = LineJoin.Round })
            using (var words = new SolidBrush(Color.FromArgb(249, 250, 255)))
            using (var clockFamily = FontFamily.GenericMonospace)
            using (var metricFamily = FontFamily.GenericSansSerif)
            {
                DrawOutlined(paint, timeText, clockFamily, (float)(21 * scale),
                    new RectangleF(0, edge, size.Width, timeBand), format, stroke, words);
                DrawOutlined(paint, metricsText, metricFamily, (float)(12 * scale),
                    new RectangleF(0, edge + timeBand, size.Width, size.Height - edge - timeBand), format, stroke, words);
            }
        }

        private static void DrawOutlined(Graphics paint, string text, FontFamily font, float size,
            RectangleF area, StringFormat format, Pen stroke, Brush fill)
        {
            if (text.Length == 0 || area.Height <= 0) return;
            using (var outline = new GraphicsPath())
            {
                outline.AddString(text, font, (int)FontStyle.Bold, size, area, format);
                paint.DrawPath(stroke, outline);
                paint.FillPath(fill, outline);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) mouseDown = e.Location;
        }

        protected override void OnMouseHover(EventArgs e)
        {
            base.OnMouseHover(e);
            if (!hoverDismissed && !dragging && !quotaMenu.Visible && !mouseDown.HasValue)
                ShowQuotaHint();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverDismissed = false;
            if (!quotaPinned) CloseQuotaHint();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button != MouseButtons.Left || !mouseDown.HasValue) return;
            var press = mouseDown.Value;
            var drag = SystemInformation.DragSize;
            if (Math.Abs(press.X - e.X) < drag.Width / 2 && Math.Abs(press.Y - e.Y) < drag.Height / 2) return;
            mouseDown = null;
            CloseQuotaHint();
            Capture = false;
            var screen = PointToScreen(e.Location);
            int position = (screen.Y & 0xffff) << 16 | (screen.X & 0xffff);
            const int nonClientLeftDown = 0x00A1;
            const int caption = 2;
            SendMessage(Handle, nonClientLeftDown, new IntPtr(caption), new IntPtr(position));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !mouseDown.HasValue) return;
            mouseDown = null;
            if (!ClientRectangle.Contains(e.Location)) return;
            if (quotaPinned)
            {
                CloseQuotaHint();
                hoverDismissed = ClientRectangle.Contains(PointToClient(Cursor.Position));
            }
            else
            {
                quotaPinned = true;
                ShowQuotaHint();
            }
        }

        private void ShowQuotaHint()
        {
            if (!Visible || quotas == null) return;
            using (PetDisplays.PhysicalCoordinates.Enter())
            {
                var bounds = PetDisplays.WindowBounds(Handle);
                var display = PetDisplays.ForBounds(bounds);
                if (display.Handle == IntPtr.Zero || bounds.IsEmpty) return;
                if (quotaPopup == null) quotaPopup = new QuotaPopup();
                quotaPopup.ShowNear(this, bounds, display.WorkArea, (double)bounds.Width / BaseSize.Width,
                    quotas.FiveHourRows, quotaPinned);
            }
        }

        internal void CloseQuotaHint()
        {
            quotaPinned = false;
            if (quotaPopup != null && quotaPopup.Visible) quotaPopup.Hide();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible)
            {
                CloseQuotaHint();
                hoverDismissed = false;
                mouseDown = null;
            }
            base.OnVisibleChanged(e);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (quotaPopup == null || !quotaPopup.Visible) return;
            if (quotaPinned) ShowQuotaHint();
            else CloseQuotaHint();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (quotaPopup != null) quotaPopup.Dispose();
                quotaMenu.Dispose();
                if (cachedArt != null) cachedArt.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class QuotaPopup : Form
        {
            private const int HeaderHeight = 38;
            private FiveHourQuota[] rows = new FiveHourQuota[0];
            private double scale = 1;
            private bool pinned;

            internal QuotaPopup()
            {
                Text = "5 小时额度";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                AutoScaleMode = AutoScaleMode.None;
                TopMost = true;
                BackColor = Board.Canvas;
                AccessibleName = Text;
                DoubleBuffered = true;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= 0x80 | 0x8000000;
                    return parameters;
                }
            }

            internal void ShowNear(Form owner, Rectangle ownerBounds, Rectangle work, double newScale,
                FiveHourQuota[] values, bool fixedOpen)
            {
                bool scaleChanged = scale != newScale;
                scale = newScale;
                rows = values;
                pinned = fixedOpen;
                AccessibleDescription = string.Join("\n", rows.Select(row => row.Name + " " + row.Remaining + " " + row.Note));
                int gap = Pixels(8);
                int height = Pixels(HeaderHeight) + rows.Sum(row => Pixels(RowHeight(row))) + Pixels(9);
                var size = new Size(Math.Min(Pixels(288), work.Width), Math.Min(height, work.Height));
                if (scaleChanged || size != Size || Region == null)
                {
                    using (var outline = CardOutline(new Rectangle(Point.Empty, size)))
                    {
                        var old = Region;
                        Region = new Region(outline);
                        if (old != null) old.Dispose();
                    }
                }

                int x = ownerBounds.Right + gap;
                int y = ownerBounds.Top + gap;
                if (x + size.Width > work.Right) x = ownerBounds.Left - size.Width - gap;
                if (x < work.Left)
                {
                    x = ownerBounds.Left;
                    y = ownerBounds.Top - size.Height - gap;
                    if (y < work.Top) y = ownerBounds.Bottom + gap;
                }
                Bounds = PetDisplays.Fit(work, new Point(x, y), size);
                if (!Visible) Show(owner);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var paint = e.Graphics;
                paint.SmoothingMode = SmoothingMode.AntiAlias;
                int side = Pixels(13);
                int width = ClientSize.Width - 2 * side;
                if (width <= 0) return;
                using (var outline = CardOutline(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1)))
                using (var border = new Pen(Board.Divider))
                using (var accent = new SolidBrush(Board.Lime))
                using (var track = new SolidBrush(Board.Surface))
                using (var title = PopupFont(15, FontStyle.Bold))
                using (var name = PopupFont(13, FontStyle.Regular))
                using (var note = PopupFont(11, FontStyle.Regular))
                using (var amount = PopupFont(18, FontStyle.Bold))
                {
                    paint.DrawPath(border, outline);
                    paint.FillRectangle(accent, side, Pixels(13), Pixels(3), Pixels(12));
                    var header = new Rectangle(side + Pixels(10), 0, width - Pixels(10), Pixels(HeaderHeight));
                    Board.Draw(paint, Text, title, Board.Ink, header);
                    if (pinned) Board.Draw(paint, "已固定", note, Board.Muted, header, true);
                    paint.DrawLine(border, side, Pixels(HeaderHeight) - 1, side + width, Pixels(HeaderHeight) - 1);

                    int top = Pixels(HeaderHeight);
                    foreach (var row in rows)
                    {
                        int rowHeight = Pixels(RowHeight(row));
                        int valueWidth = Pixels(85);
                        Board.Draw(paint, row.Name, name, Board.Ink,
                            new Rectangle(side, top + Pixels(6), width - valueWidth, Pixels(22)));
                        Board.Draw(paint, row.Remaining, row.Percent.HasValue ? amount : name,
                            row.Percent.HasValue ? Board.Lime : Board.Muted,
                            new Rectangle(side + width - valueWidth, top + Pixels(4), valueWidth, Pixels(26)), true);
                        if (!string.IsNullOrEmpty(row.Note))
                            Board.Draw(paint, row.Note, note, Board.Muted,
                                new Rectangle(side, top + Pixels(28), width, Pixels(13)));
                        if (row.Percent.HasValue)
                        {
                            int line = top + rowHeight - Pixels(4);
                            paint.FillRectangle(track, side, line, width, Pixels(2));
                            int fill = (int)Math.Round(width * Math.Max(0, Math.Min(100, row.Percent.Value)) / 100);
                            paint.FillRectangle(accent, side, line, fill, Pixels(2));
                        }
                        top += rowHeight;
                    }
                }
            }

            private static int RowHeight(FiveHourQuota row)
            {
                return row.Percent.HasValue ? 51 : string.IsNullOrEmpty(row.Note) ? 34 : 44;
            }

            private int Pixels(int value) { return Scaled(value, scale); }

            private Font PopupFont(int size, FontStyle style)
            {
                return new Font("Segoe UI", (float)(size * scale), style, GraphicsUnit.Pixel);
            }

            private GraphicsPath CardOutline(Rectangle bounds)
            {
                int arc = Math.Max(1, Math.Min(Pixels(24), Math.Min(bounds.Width, bounds.Height)));
                var path = new GraphicsPath();
                path.AddArc(bounds.Left, bounds.Top, arc, arc, 180, 90);
                path.AddArc(bounds.Right - arc, bounds.Top, arc, arc, 270, 90);
                path.AddArc(bounds.Right - arc, bounds.Bottom - arc, arc, arc, 0, 90);
                path.AddArc(bounds.Left, bounds.Bottom - arc, arc, arc, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int dpiChanged = 0x02E0;
            const int enterSizeMove = 0x0231;
            const int exitSizeMove = 0x0232;
            if (message.Msg == dpiChanged)
            {
                if (!drawingSprite && IsHandleCreated && message.LParam != IntPtr.Zero)
                {
                    var suggested = (PetDisplays.NativeRect)Marshal.PtrToStructure(message.LParam, typeof(PetDisplays.NativeRect));
                    DrawSprite(suggested.ToRectangle());
                }
                return;
            }
            if (message.Msg == enterSizeMove) dragging = true;
            if (message.Msg == exitSizeMove) dragging = false;
            base.WndProc(ref message);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Blend { internal byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref Point dst,
            ref Size dimensions, IntPtr srcDc, ref Point src, int key, ref Blend blend, uint flags);
    }
}
