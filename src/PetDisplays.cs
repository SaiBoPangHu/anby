using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace AnbyPet
{
    internal static class PetDisplays
    {
        private const uint NearestMonitor = 2;
        private const uint RootWindow = 2;
        private const int CaptionStyle = 0x00C00000;
        private static readonly int RectSize = Marshal.SizeOf(typeof(NativeRect));

        internal struct Display
        {
            internal IntPtr Handle;
            internal Rectangle Bounds, WorkArea;

            internal double Scale()
            {
                // Read the same effective DPI a real window on this display would receive.
                var center = new Point(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
                var probe = CreateWindowEx(0, "STATIC", null, 0x80000000, center.X, center.Y, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (probe == IntPtr.Zero) return 1.0;
                try
                {
                    uint dpi = GetDpiForWindow(probe);
                    return dpi == 0 ? 1.0 : dpi / 96.0;
                }
                finally { DestroyWindow(probe); }
            }
        }

        internal struct PhysicalCoordinates : IDisposable
        {
            private readonly IntPtr previous;

            private PhysicalCoordinates(IntPtr previous)
            {
                this.previous = previous;
            }

            internal static PhysicalCoordinates Enter()
            {
                var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
                if (previous == IntPtr.Zero) previous = SetThreadDpiAwarenessContext(new IntPtr(-3));
                return new PhysicalCoordinates(previous);
            }

            public void Dispose()
            {
                if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
            }
        }

        internal static Display AtCursor()
        {
            Point point;
            GetCursorPos(out point);
            return Read(MonitorFromPoint(point, NearestMonitor));
        }

        internal static Display Primary()
        {
            return Read(MonitorFromPoint(Point.Empty, NearestMonitor));
        }

        internal static Display Secondary(Display primary)
        {
            Display secondary = new Display();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr value)
            {
                if (monitor == primary.Handle) return true;
                var candidate = Read(monitor);
                if (candidate.Handle == IntPtr.Zero || candidate.Bounds == primary.Bounds) return true;
                secondary = candidate;
                return false;
            }, IntPtr.Zero);
            return secondary;
        }

        internal static Display ForBounds(Rectangle rectangle)
        {
            var bounds = new NativeRect { Left = rectangle.Left, Top = rectangle.Top, Right = rectangle.Right, Bottom = rectangle.Bottom };
            return Read(MonitorFromRect(ref bounds, NearestMonitor));
        }

        internal static Rectangle WindowBounds(IntPtr window)
        {
            NativeRect bounds;
            return GetWindowRect(window, out bounds) ? bounds.ToRectangle() : Rectangle.Empty;
        }

        internal static bool MoveWindow(IntPtr window, Point position)
        {
            const uint keepSizeAndOrder = 0x0001 | 0x0004 | 0x0010;
            return SetWindowPos(window, IntPtr.Zero, position.X, position.Y, 0, 0, keepSizeAndOrder);
        }

        internal static Rectangle Fit(Rectangle work, Point position, Size size)
        {
            int x = Math.Max(work.Left, Math.Min(position.X, work.Right - size.Width));
            int y = Math.Max(work.Top, Math.Min(position.Y, work.Bottom - size.Height));
            return new Rectangle(new Point(x, y), size);
        }

        internal static Rectangle BottomLeft(Rectangle work, Size size, int inset, Rectangle occupied)
        {
            var corner = Fit(work, new Point(work.Left + inset, work.Bottom - size.Height - inset), size);
            if (!corner.IntersectsWith(occupied)) return corner;
            if (occupied.Right + inset + size.Width <= work.Right)
                corner.X = occupied.Right + inset;
            else if (occupied.Top - inset - size.Height >= work.Top)
                corner.Y = occupied.Top - inset - size.Height;
            return corner;
        }

        internal static bool PrimaryIsFullscreen(Display primary, int ownProcess)
        {
            var foreground = Root(GetForegroundWindow());
            int shell = ProcessId(GetShellWindow());
            Rectangle bounds;
            if (AppBounds(foreground, ownProcess, shell, out bounds) && FillsDisplay(foreground, bounds, primary))
                return true;
            // Look through the visible part of the primary when focus has moved away from its fullscreen window.
            var center = new Point(primary.Bounds.Left + primary.Bounds.Width / 2, primary.Bounds.Top + primary.Bounds.Height / 2);
            var visible = Root(WindowFromPoint(center));
            if (ProcessId(visible) == ownProcess)
                visible = Root(WindowFromPoint(new Point(primary.Bounds.Left + primary.Bounds.Width / 4, center.Y)));
            return visible != foreground && AppBounds(visible, ownProcess, shell, out bounds) &&
                FillsDisplay(visible, bounds, primary);
        }

        private static Display Read(IntPtr handle)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (handle != IntPtr.Zero && GetMonitorInfo(handle, ref info))
                return new Display { Handle = handle, Bounds = info.Bounds.ToRectangle(), WorkArea = info.WorkArea.ToRectangle() };
            return new Display();
        }

        private static IntPtr Root(IntPtr window)
        {
            return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, RootWindow);
        }

        private static int ProcessId(IntPtr window)
        {
            int process = 0;
            if (window != IntPtr.Zero) GetWindowThreadProcessId(window, out process);
            return process;
        }

        private static bool AppBounds(IntPtr window, int ownProcess, int shell, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            int process = ProcessId(window);
            if (process == 0 || process == ownProcess || process == shell || !IsWindowVisible(window) || IsIconic(window))
                return false;
            NativeRect outer;
            const int extendedFrameBounds = 9;
            if (DwmGetWindowAttribute(window, extendedFrameBounds, out outer, RectSize) == 0)
                bounds = outer.ToRectangle();
            if (bounds.Width <= 0 || bounds.Height <= 0) bounds = WindowBounds(window);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        private static bool FillsDisplay(IntPtr window, Rectangle bounds, Display display)
        {
            const int frameTolerance = 2;
            const int style = -16;
            if (display.WorkArea == display.Bounds && IsZoomed(window) &&
                (GetWindowLong(window, style) & CaptionStyle) == CaptionStyle) return false;
            return bounds.Left <= display.Bounds.Left + frameTolerance && bounds.Top <= display.Bounds.Top + frameTolerance &&
                bounds.Right >= display.Bounds.Right - frameTolerance && bounds.Bottom >= display.Bounds.Bottom - frameTolerance;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left, Top, Right, Bottom;
            internal Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Bounds, WorkArea;
            internal uint Flags;
        }

        private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr awareness);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extendedStyle,
            string windowClass, string title, uint style, int x, int y, int width, int height, IntPtr parent,
            IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y,
            int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int length);
    }
}
