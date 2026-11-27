using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace AnbyPet
{
    internal enum BurgerStage
    {
        Waiting,
        Empty,
        Bite,
        Half,
        Large,
        Full
    }

    internal static class PetArtwork
    {
        private const int CanvasSize = 1254;
        private static readonly Rectangle BurgerSource = Rectangle.FromLTRB(348, 700, 640, 997);

        internal static BurgerStage Stage(double? percent)
        {
            if (!percent.HasValue) return BurgerStage.Waiting;
            if (percent <= 0) return BurgerStage.Empty;
            if (percent < 25) return BurgerStage.Bite;
            if (percent < 50) return BurgerStage.Half;
            return percent < 75 ? BurgerStage.Large : BurgerStage.Full;
        }

        internal static Bitmap Render(int edge, BurgerStage stage)
        {
            var result = new Bitmap(edge, edge, PixelFormat.Format32bppPArgb);
            try
            {
                using (var graphics = Graphics.FromImage(result))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    string resource = stage == BurgerStage.Full ? "AnbyPet.PetFull.png" : "AnbyPet.PetEmpty.png";
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                    using (var sprite = Image.FromStream(stream))
                        graphics.DrawImage(sprite, new Rectangle(0, 0, edge, edge));

                    if (stage != BurgerStage.Full && stage != BurgerStage.Empty)
                        DrawBurger(graphics, edge, stage);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static void DrawBurger(Graphics graphics, int edge, BurgerStage stage)
        {
            double fraction;
            switch (stage)
            {
                case BurgerStage.Bite: fraction = 0.38; break;
                case BurgerStage.Half: fraction = 0.62; break;
                case BurgerStage.Large: fraction = 0.81; break;
                default: fraction = 0.78; break;
            }
            double scale = (double)edge / CanvasSize;
            int width = Math.Max(1, (int)Math.Round(BurgerSource.Width * scale * fraction));
            int height = Math.Max(1, (int)Math.Round(BurgerSource.Height * scale * fraction));
            var bounds = new Rectangle((int)Math.Round(494 * scale - width / 2.0),
                (int)Math.Round(868 * scale - height / 2.0), width, height);

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AnbyPet.PetFull.png"))
            using (var sprite = Image.FromStream(stream))
            using (var burger = new Bitmap(BurgerSource.Width, BurgerSource.Height, PixelFormat.Format32bppPArgb))
            using (var outline = BurgerOutline())
            using (var attributes = new ImageAttributes())
            {
                using (var cutout = Graphics.FromImage(burger))
                {
                    cutout.TranslateTransform(-BurgerSource.X, -BurgerSource.Y);
                    cutout.SetClip(outline);
                    cutout.DrawImageUnscaled(sprite, 0, 0);
                }
                if (stage == BurgerStage.Waiting)
                {
                    attributes.SetColorMatrix(new ColorMatrix(new[]
                    {
                        new[] { 0.24f, 0.24f, 0.24f, 0f, 0f },
                        new[] { 0.49f, 0.49f, 0.49f, 0f, 0f },
                        new[] { 0.09f, 0.09f, 0.09f, 0f, 0f },
                        new[] { 0f, 0f, 0f, 1f, 0f },
                        new[] { 0.04f, 0.04f, 0.04f, 0f, 1f }
                    }));
                }
                graphics.DrawImage(burger, bounds, 0, 0, burger.Width, burger.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        private static GraphicsPath BurgerOutline()
        {
            // Trace the visible food between the hands of the mirrored sprite.
            var path = new GraphicsPath();
            path.AddBezier(352, 790, 362, 745, 412, 704, 467, 704);
            path.AddBezier(467, 704, 481, 724, 506, 729, 533, 714);
            path.AddBezier(533, 714, 532, 736, 545, 741, 558, 738);
            path.AddBezier(558, 738, 568, 754, 581, 760, 594, 752);
            path.AddBezier(594, 752, 617, 770, 630, 797, 634, 820);
            path.AddBezier(634, 820, 589, 804, 550, 827, 544, 860);
            path.AddBezier(544, 860, 536, 887, 551, 911, 577, 925);
            path.AddBezier(577, 925, 578, 943, 572, 954, 579, 972);
            path.AddBezier(579, 972, 543, 999, 411, 1004, 368, 962);
            path.AddBezier(368, 962, 357, 947, 365, 916, 359, 898);
            path.AddBezier(359, 898, 410, 884, 429, 825, 352, 790);
            path.CloseFigure();
            return path;
        }
    }
}
