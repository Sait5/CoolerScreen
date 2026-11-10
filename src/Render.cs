using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CoolerScreen
{
    public static class Render
    {
        public const int S = 320;
        static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        public static byte[] Bgra(Bitmap bmp)
        {
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var arr = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(d.Scan0 + y * d.Stride, arr, y * bmp.Width * 4, bmp.Width * 4);
            bmp.UnlockBits(d);
            return arr;
        }

        internal static Graphics G(Bitmap b)
        {
            var g = Graphics.FromImage(b);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }

        internal static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // цвет по температуре: акцент -> оранжевый -> красный
        internal static Color Heat(Color accent, float temp)
        {
            if (temp < 60) return accent;
            if (temp < 80) return Mix(accent, Color.FromArgb(255, 170, 40), (temp - 60) / 20f);
            return Mix(Color.FromArgb(255, 170, 40), Color.FromArgb(255, 60, 60), (temp - 80) / 15f);
        }

        internal static void Center(Graphics g, string s, Font f, Brush b, float cx, float y)
        {
            var sz = g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);
            g.DrawString(s, f, b, cx - sz.Width / 2, y, StringFormat.GenericTypographic);
        }

        internal static void Ring(Graphics g, float cx, float cy, float r, float frac, Color col, string val, string unit, string label, string sub)
        {
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            using (var p = new Pen(Color.FromArgb(34, 40, 56), 11)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawArc(p, rect, 135, 270); }
            frac = Math.Max(0.01f, Math.Min(1, frac));
            using (var glow = new Pen(Color.FromArgb(50, col), 19)) { glow.StartCap = glow.EndCap = LineCap.Round; g.DrawArc(glow, rect, 135, 270 * frac); }
            using (var p = new Pen(col, 11)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawArc(p, rect, 135, 270 * frac); }

            using (var fv = new Font("Segoe UI Semibold", 30, GraphicsUnit.Pixel))
            using (var fu = new Font("Segoe UI", 15, GraphicsUnit.Pixel))
            {
                var vs = g.MeasureString(val, fv, PointF.Empty, StringFormat.GenericTypographic);
                var us = g.MeasureString(unit, fu, PointF.Empty, StringFormat.GenericTypographic);
                float x = cx - (vs.Width + us.Width + 2) / 2;
                g.DrawString(val, fv, Brushes.White, x, cy - 24, StringFormat.GenericTypographic);
                using (var ub = new SolidBrush(Color.FromArgb(150, 160, 185)))
                    g.DrawString(unit, fu, ub, x + vs.Width + 2, cy - 12, StringFormat.GenericTypographic);
            }
            using (var fl = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel))
            using (var lb = new SolidBrush(col))
                Center(g, label, fl, lb, cx, cy + 12);
            if (sub != null)
                using (var fs = new Font("Segoe UI", 12, GraphicsUnit.Pixel))
                using (var sb = new SolidBrush(Color.FromArgb(120, 130, 155)))
                    Center(g, sub, fs, sb, cx, cy + r - 4);
        }

        internal static void Bar(Graphics g, float x, float y, float w, string label, string val, float frac, Color col)
        {
            using (var fl = new Font("Segoe UI", 12, GraphicsUnit.Pixel))
            using (var fv = new Font("Segoe UI Semibold", 15, GraphicsUnit.Pixel))
            using (var lb = new SolidBrush(Color.FromArgb(125, 135, 160)))
            {
                g.DrawString(label, fl, lb, x, y, StringFormat.GenericTypographic);
                g.DrawString(val, fv, Brushes.White, x, y + 15, StringFormat.GenericTypographic);
            }
            using (var tb = new SolidBrush(Color.FromArgb(34, 40, 56))) g.FillRectangle(tb, x, y + 38, w, 4);
            using (var cb = new SolidBrush(col)) g.FillRectangle(cb, x, y + 38, w * Math.Max(0.02f, Math.Min(1, frac)), 4);
        }

        public static Bitmap Dashboard(SensorData s, Color accent)
        {
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = G(bmp))
            {
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, S, S), Color.FromArgb(8, 10, 17), Color.FromArgb(14, 17, 30), 90f))
                    g.FillRectangle(bg, 0, 0, S, S);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(-60, -150, S + 120, 260);
                    using (var pg = new PathGradientBrush(path) { CenterColor = Color.FromArgb(55, accent), SurroundColors = new[] { Color.FromArgb(0, accent) } })
                        g.FillPath(pg, path);
                }

                var now = DateTime.Now;
                using (var ft = new Font("Segoe UI Light", 54, GraphicsUnit.Pixel))
                    Center(g, now.ToString("HH:mm"), ft, Brushes.White, S / 2f, 6);
                using (var fd = new Font("Segoe UI", 13, GraphicsUnit.Pixel))
                using (var db = new SolidBrush(Color.FromArgb(140, 150, 175)))
                    Center(g, now.ToString("dddd, d MMMM", Ru), fd, db, S / 2f, 70);

                Ring(g, 84, 160, 58, s.CpuLoad / 100f, accent, ((int)Math.Round(s.CpuLoad)).ToString(), "%", "ЦП", "загрузка");
                if (s.HasGpu)
                    Ring(g, 236, 160, 58, (s.GpuTemp - 25) / 75f, Heat(accent, s.GpuHotspot), ((int)s.GpuTemp).ToString(), "°C", "ГП",
                        "хотспот " + (int)s.GpuHotspot + "°");
                else
                    Ring(g, 236, 160, 58, s.RamLoad / 100f, accent, ((int)Math.Round(s.RamLoad)).ToString(), "%", "ОЗУ", null);

                float y = 252, w = 84, gap = 14, x0 = (S - (w * 3 + gap * 2)) / 2f;
                if (s.HasGpu)
                {
                    Bar(g, x0, y, w, "загрузка ГП", (int)s.GpuLoad + "%", s.GpuLoad / 100f, accent);
                    Bar(g, x0 + w + gap, y, w, "ОЗУ", s.RamUsedGb.ToString("0.0", Ru) + " ГБ", s.RamLoad / 100f, accent);
                    Bar(g, x0 + 2 * (w + gap), y, w, "мощность ГП", (int)s.GpuPower + " Вт", s.GpuPower / 220f, accent);
                }
                else
                {
                    Bar(g, x0, y, w, "ОЗУ", s.RamUsedGb.ToString("0.0", Ru) + " ГБ", s.RamLoad / 100f, accent);
                }
            }
            return bmp;
        }

        // Полоска с датчиками поверх GIF (прозрачный фон)
        public static Bitmap Overlay(SensorData s, Color accent)
        {
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = G(bmp))
            {
                g.Clear(Color.Transparent);
                var rect = new RectangleF(14, S - 62, S - 28, 48);
                using (var path = RoundRect(rect, 16))
                using (var bb = new SolidBrush(Color.FromArgb(175, 6, 8, 14)))
                    g.FillPath(bb, path);

                string[] lab, val; Color[] col;
                if (s.HasGpu)
                {
                    lab = new[] { "ГП", "ЦП", "ОЗУ", "время" };
                    val = new[] { (int)s.GpuTemp + "°", (int)Math.Round(s.CpuLoad) + "%", (int)Math.Round(s.RamLoad) + "%", DateTime.Now.ToString("HH:mm") };
                    col = new[] { Heat(accent, s.GpuHotspot), accent, accent, accent };
                }
                else
                {
                    lab = new[] { "ЦП", "ОЗУ", "время" };
                    val = new[] { (int)Math.Round(s.CpuLoad) + "%", (int)Math.Round(s.RamLoad) + "%", DateTime.Now.ToString("HH:mm") };
                    col = new[] { accent, accent, accent };
                }
                float cellW = rect.Width / lab.Length;
                using (var fl = new Font("Segoe UI", 11, GraphicsUnit.Pixel))
                using (var fv = new Font("Segoe UI Semibold", 20, GraphicsUnit.Pixel))
                    for (int i = 0; i < lab.Length; i++)
                    {
                        float cx = rect.X + cellW * i + cellW / 2;
                        using (var lb = new SolidBrush(col[i])) Center(g, lab[i], fl, lb, cx, rect.Y + 5);
                        Center(g, val[i], fv, Brushes.White, cx, rect.Y + 19);
                    }
            }
            return bmp;
        }

        internal static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        public enum Fit { Cover, Contain, Stretch }

        public static void DrawFitted(Graphics g, Image img, Fit fit)
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float iw = img.Width, ih = img.Height;
            RectangleF dst;
            if (fit == Fit.Stretch) dst = new RectangleF(0, 0, S, S);
            else
            {
                float k = fit == Fit.Cover ? Math.Max(S / iw, S / ih) : Math.Min(S / iw, S / ih);
                dst = new RectangleF((S - iw * k) / 2, (S - ih * k) / 2, iw * k, ih * k);
            }
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, Rectangle.Round(dst), 0, 0, iw, ih, GraphicsUnit.Pixel, ia);
            }
        }
    }
}
