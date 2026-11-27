using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AnbyPet
{
    internal sealed class DesktopPet : Form
    {
        private static readonly Size BaseSize = new Size(185, 278);
        private Rectangle? returnBounds;
        private Rectangle destinationArea, destinationObstacle;
        private bool dragging, drawingSprite;

        internal DesktopPet()
        {
            Text = "安比";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = BaseSize;
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
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AnbyPet.Sprite.png"))
                using (var sprite = Image.FromStream(stream))
                using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb))
                {
                    using (var paint = Graphics.FromImage(bitmap))
                    {
                        paint.CompositingMode = CompositingMode.SourceCopy;
                        paint.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        paint.DrawImage(sprite, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
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
            if (message.Msg == 0x84 && message.Result.ToInt32() == 1) message.Result = new IntPtr(2);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Blend { internal byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref Point dst,
            ref Size dimensions, IntPtr srcDc, ref Point src, int key, ref Blend blend, uint flags);
    }
}
