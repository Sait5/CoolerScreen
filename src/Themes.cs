using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace CoolerScreen
{
    // История датчиков за последнюю минуту (для графиков)
    public sealed class History
    {
        public const int N = 60;
        public readonly float[] Cpu = new float[N], GpuTemp = new float[N], GpuLoad = new float[N];
        public int Count;
        public void Push(SensorData d)
        {
            lock (this)
            {
                Array.Copy(Cpu, 1, Cpu, 0, N - 1); Cpu[N - 1] = d.CpuLoad;
                Array.Copy(GpuTemp, 1, GpuTemp, 0, N - 1); GpuTemp[N - 1] = d.GpuTemp;
                Array.Copy(GpuLoad, 1, GpuLoad, 0, N - 1); GpuLoad[N - 1] = d.GpuLoad;
                if (Count < N) Count++;
            }
        }
    }

    // Темы оформления экрана водянки
    public static class Themes
    {
        public static readonly string[] Names = { "Классика", "Спидометр", "Неон", "Минимал", "Терминал", "Кольца", "Графики", "Часы", "Жидкость" };
        const int S = Render.S;
        static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        // как часто перерисовывать (мс): анимированным темам — чаще
        public static int Interval(int theme) { return theme == 8 ? 90 : theme == 4 ? 500 : 1000; }

        public static Bitmap Draw(int theme, SensorData s, History h, Color a)
        {
            switch (theme)
            {
                case 1: return Speedo(s, a);
                case 2: return Neon(s, a);
                case 3: return Minimal(s, a);
                case 4: return Terminal(s, a);
                case 5: return Rings(s, a);
                case 6: return Graphs(s, h, a);
                case 7: return Clock(s, a);
                case 8: return Liquid(s, a);
                default: return Render.Dashboard(s, a);
            }
        }

        static Bitmap New(out Graphics g, Color bg)
        {
            var b = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            g = Render.G(b);
            g.Clear(bg);
            return b;
        }

        static Font F(string fam, float px, FontStyle st = FontStyle.Regular) { return new Font(fam, px, st, GraphicsUnit.Pixel); }
        static SolidBrush B(Color c) { return new SolidBrush(c); }
        static Color A(int alpha, Color c) { return Color.FromArgb(alpha, c); }

        static Color HueShift(Color c, float deg)
        {
            float h = c.GetHue() + deg, sat = Math.Max(0.6f, c.GetSaturation()), l = Math.Min(0.62f, Math.Max(0.5f, c.GetBrightness()));
            h = ((h % 360) + 360) % 360;
            float q = l < 0.5f ? l * (1 + sat) : l + sat - l * sat, p = 2 * l - q;
            Func<float, int> f = t =>
            {
                t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
                float v = t < 1 / 6f ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2 / 3f ? p + (q - p) * (2 / 3f - t) * 6 : p;
                return (int)Math.Round(v * 255);
            };
            return Color.FromArgb(f(h / 360 + 1 / 3f), f(h / 360), f(h / 360 - 1 / 3f));
        }

        static float MainTemp(SensorData s) { return s.HasGpu ? s.GpuTemp : s.CpuLoad; }
        static string MainLabel(SensorData s) { return s.HasGpu ? "ГП" : "ЦП"; }

        static void Glow(Graphics g, Color c)
        {
        }

        static void BgGlow(Graphics g, Color c, int alpha, RectangleF r)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (var pg = new PathGradientBrush(path) { CenterColor = A(alpha, c), SurroundColors = new[] { A(0, c) } })
                    g.FillPath(pg, path);
            }
        }

        // ---------- 1. Спидометр ----------
        static Bitmap Speedo(SensorData s, Color a)
        {
            Graphics g; var bmp = New(out g, Color.FromArgb(8, 9, 14));
            using (g)
            {
                BgGlow(g, a, 45, new RectangleF(10, 20, 300, 300));
                float cx = 160, cy = 168, r = 128;
                float val = MainTemp(s), lo = s.HasGpu ? 20 : 0, hi = s.HasGpu ? 100 : 100;
                float frac = Math.Max(0, Math.Min(1, (val - lo) / (hi - lo)));
                var heat = Render.Heat(a, s.HasGpu ? s.GpuHotspot : 0);

                using (var p = new Pen(Color.FromArgb(30, 34, 48), 12)) g.DrawArc(p, cx - r, cy - r, r * 2, r * 2, 135, 270);
                using (var p = new Pen(heat, 12)) g.DrawArc(p, cx - r, cy - r, r * 2, r * 2, 135, 270 * frac);

                // деления
                using (var fnum = F("Segoe UI Semibold", 13))
                using (var pMaj = new Pen(Color.FromArgb(200, 210, 225), 2.5f))
                using (var pMin = new Pen(Color.FromArgb(90, 100, 120), 1.5f))
                using (var bn = B(Color.FromArgb(140, 150, 170)))
                    for (int i = 0; i <= 40; i++)
                    {
                        double ang = (135 + 270.0 * i / 40) * Math.PI / 180;
                        bool major = i % 5 == 0;
                        float r1 = r - 14, r2 = major ? r - 30 : r - 22;
                        g.DrawLine(major ? pMaj : pMin, cx + (float)Math.Cos(ang) * r1, cy + (float)Math.Sin(ang) * r1, cx + (float)Math.Cos(ang) * r2, cy + (float)Math.Sin(ang) * r2);
                        if (major && i % 10 == 0)
                        {
                            string t = ((int)(lo + (hi - lo) * i / 40)).ToString();
                            float rt = r - 44;
                            var sz = g.MeasureString(t, fnum);
                            g.DrawString(t, fnum, bn, cx + (float)Math.Cos(ang) * rt - sz.Width / 2, cy + (float)Math.Sin(ang) * rt - sz.Height / 2);
                        }
                    }

                // стрелка
                double na = (135 + 270 * frac) * Math.PI / 180;
                using (var p = new Pen(A(70, heat), 9) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, cx, cy, cx + (float)Math.Cos(na) * (r - 34), cy + (float)Math.Sin(na) * (r - 34));
                using (var p = new Pen(heat, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, cx - (float)Math.Cos(na) * 14, cy - (float)Math.Sin(na) * 14, cx + (float)Math.Cos(na) * (r - 34), cy + (float)Math.Sin(na) * (r - 34));
                using (var b = B(Color.FromArgb(22, 25, 36))) g.FillEllipse(b, cx - 15, cy - 15, 30, 30);
                using (var p = new Pen(heat, 3)) g.DrawEllipse(p, cx - 15, cy - 15, 30, 30);

                using (var fv = F("Segoe UI Semibold", 34))
                    Render.Center(g, ((int)val) + (s.HasGpu ? "°" : "%"), fv, Brushes.White, cx, cy + 34);
                using (var fl = F("Segoe UI", 12))
                using (var bl = B(Color.FromArgb(140, 150, 170)))
                    Render.Center(g, s.HasGpu ? "ГП · хотспот " + (int)s.GpuHotspot + "°" : "загрузка ЦП", fl, bl, cx, cy + 76);
                using (var fs = F("Segoe UI Semibold", 15))
                using (var bs = B(Color.FromArgb(200, 208, 222)))
                {
                    g.DrawString("ЦП " + (int)Math.Round(s.CpuLoad) + "%", fs, bs, 26, 288);
                    var t = "ОЗУ " + (int)Math.Round(s.RamLoad) + "%";
                    var sz = g.MeasureString(t, fs);
                    g.DrawString(t, fs, bs, S - 26 - sz.Width, 288);
                }
            }
            return bmp;
        }

        // ---------- 2. Неон ----------
        static void NeonText(Graphics g, string t, string fam, float px, Color c, float cx, float y)
        {
            using (var path = new GraphicsPath())
            using (var ff = new FontFamily(fam))
            {
                path.AddString(t, ff, (int)FontStyle.Regular, px, PointF.Empty, StringFormat.GenericTypographic);
                var bounds = path.GetBounds();
                using (var m = new Matrix()) { m.Translate(cx - bounds.Width / 2 - bounds.X, y); path.Transform(m); }
                foreach (var w in new[] { 14f, 9f, 5f })
                    using (var p = new Pen(A(w > 10 ? 25 : w > 6 ? 50 : 110, c), w) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
                using (var b = B(Render.Mix(c, Color.White, 0.75f))) g.FillPath(b, path);
            }
        }

        static void NeonLine(Graphics g, Color c, float x1, float y1, float x2, float y2)
        {
            foreach (var w in new[] { 10f, 5f })
                using (var p = new Pen(A(w > 6 ? 30 : 80, c), w) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, x1, y1, x2, y2);
            using (var p = new Pen(Render.Mix(c, Color.White, 0.6f), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, x1, y1, x2, y2);
        }

        static Bitmap Neon(SensorData s, Color a)
        {
            Graphics g; var bmp = New(out g, Color.FromArgb(4, 3, 10));
            using (g)
            {
                var b2 = HueShift(a, 150);
                // сетка-перспектива снизу
                using (var p = new Pen(A(40, b2), 1))
                {
                    for (int i = 0; i < 7; i++) { float y = 250 + i * i * 2.2f; g.DrawLine(p, 0, y, S, y); }
                    for (int i = -8; i <= 8; i++) g.DrawLine(p, 160 + i * 12, 250, 160 + i * 60, 330);
                }
                NeonText(g, DateTime.Now.ToString("HH:mm"), "Segoe UI Light", 58, a, 160, 18);
                NeonLine(g, a, 40, 96, 280, 96);
                NeonText(g, ((int)MainTemp(s)) + (s.HasGpu ? "°" : "%"), "Segoe UI Semibold", 64, b2, 92, 116);
                NeonText(g, ((int)Math.Round(s.CpuLoad)) + "%", "Segoe UI Semibold", 64, a, 232, 116);
                using (var fl = F("Segoe UI Semibold", 13))
                {
                    using (var bb = B(b2)) Render.Center(g, s.HasGpu ? "ТЕМП. ГП" : "ЦП", fl, bb, 92, 196);
                    using (var bb = B(a)) Render.Center(g, "ЦП", fl, bb, 232, 196);
                }
                NeonLine(g, b2, 160, 120, 160, 190);
                using (var fs = F("Segoe UI", 13))
                using (var bs = B(Color.FromArgb(190, 200, 230)))
                    Render.Center(g, "ОЗУ " + (int)Math.Round(s.RamLoad) + "%" + (s.HasGpu ? "   ·   ГП " + (int)s.GpuLoad + "%   ·   " + (int)s.GpuPower + " Вт" : ""), fs, bs, 160, 222);
            }
            return bmp;
        }

        // ---------- 3. Минимал ----------
        static Bitmap Minimal(SensorData s, Color a)
        {
            Graphics g; var bmp = New(out g, Color.FromArgb(6, 7, 10));
            using (g)
            {
                var now = DateTime.Now;
                using (var fd = F("Segoe UI", 13))
                using (var bd = B(Color.FromArgb(120, 128, 145)))
                    Render.Center(g, now.ToString("dddd, d MMMM", Ru).ToUpper(Ru), fd, bd, 160, 34);
                using (var ft = F("Segoe UI Light", 104))
                    Render.Center(g, now.ToString("HH:mm"), ft, Brushes.White, 160, 54);
                using (var p = new Pen(a, 2)) g.DrawLine(p, 136, 188, 184, 188);

                string[] lab, val;
                if (s.HasGpu) { lab = new[] { "ГП", "ЦП", "ОЗУ" }; val = new[] { (int)s.GpuTemp + "°", (int)Math.Round(s.CpuLoad) + "%", (int)Math.Round(s.RamLoad) + "%" }; }
                else { lab = new[] { "ЦП", "ОЗУ" }; val = new[] { (int)Math.Round(s.CpuLoad) + "%", (int)Math.Round(s.RamLoad) + "%" }; }
                float cw = 260f / lab.Length;
                using (var fl = F("Segoe UI Semibold", 11))
                using (var fv = F("Segoe UI Light", 34))
                using (var bl = B(a))
                    for (int i = 0; i < lab.Length; i++)
                    {
                        float cx = 30 + cw * i + cw / 2;
                        Render.Center(g, lab[i], fl, bl, cx, 214);
                        Render.Center(g, val[i], fv, Brushes.White, cx, 232);
                    }
            }
            return bmp;
        }

        // ---------- 4. Терминал ----------
        static string Bar10(float frac)
        {
            int n = (int)Math.Round(Math.Max(0, Math.Min(1, frac)) * 10);
            return "[" + new string('#', n) + new string('-', 10 - n) + "]";
        }

        static Bitmap Terminal(SensorData s, Color a)
        {
            Graphics g; var bmp = New(out g, Color.FromArgb(4, 7, 5));
            using (g)
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                var c = a;
                var dim = A(150, c);
                var up = TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
                var lines = new System.Collections.Generic.List<string[]>();
                lines.Add(new[] { "aio@cooler:~$ sensors", "" });
                if (s.HasGpu)
                {
                    lines.Add(new[] { "gpu.edge   ", ((int)s.GpuTemp).ToString().PadLeft(3) + " C " + Bar10((s.GpuTemp - 20) / 80) });
                    lines.Add(new[] { "gpu.hotspot", ((int)s.GpuHotspot).ToString().PadLeft(3) + " C" });
                    lines.Add(new[] { "gpu.load   ", ((int)s.GpuLoad).ToString().PadLeft(3) + " % " + Bar10(s.GpuLoad / 100) });
                    lines.Add(new[] { "gpu.power  ", ((int)s.GpuPower).ToString().PadLeft(3) + " W" });
                }
                lines.Add(new[] { "cpu.load   ", ((int)Math.Round(s.CpuLoad)).ToString().PadLeft(3) + " % " + Bar10(s.CpuLoad / 100) });
                lines.Add(new[] { "ram        ", s.RamUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + "/" + s.RamTotalGb.ToString("0", CultureInfo.InvariantCulture) + " GB" });
                lines.Add(new[] { "uptime     ", (int)up.TotalDays + "d " + up.Hours.ToString("00") + ":" + up.Minutes.ToString("00") });
                lines.Add(new[] { "time       ", DateTime.Now.ToString("HH:mm:ss") });
                using (var f = F("Consolas", 15))
                using (var bc = B(c))
                using (var bd = B(dim))
                {
                    float y = 22;
                    foreach (var l in lines)
                    {
                        if (l[1] == "") g.DrawString(l[0], f, bc, 16, y);
                        else
                        {
                            g.DrawString(l[0], f, bd, 16, y);
                            g.DrawString(l[1], f, bc, 16 + 12 * 8.3f, y);
                        }
                        y += l[1] == "" ? 30 : 23;
                    }
                    g.DrawString("aio@cooler:~$", f, bc, 16, y + 8);
                    if (DateTime.Now.Millisecond < 500) g.FillRectangle(bc, 16 + 14 * 8.3f, y + 11, 9, 16);
                }
                // строки развёртки, как у старого монитора
                using (var sl = B(Color.FromArgb(55, 0, 0, 0)))
                    for (int y = 0; y < S; y += 3) g.FillRectangle(sl, 0, y, S, 1);
                BgGlow(g, c, 18, new RectangleF(-40, -40, 400, 400));
            }
            return bmp;
        }

        // ---------- 5. Кольца ----------
        static Bitmap Rings(SensorData s, Color a)
        {
            Graphics g; var bmp = New(out g, Color.FromArgb(7, 8, 12));
            using (g)
            {
                var c1 = a; var c2 = HueShift(a, 110); var c3 = HueShift(a, -110);
                float cx = 160, cy = 146;
                float[] r = { 124, 98, 72 };
                float[] v = { s.CpuLoad / 100f, s.HasGpu ? s.GpuLoad / 100f : 0, s.RamLoad / 100f };
                Color[] cl = { c1, c2, c3 };
                for (int i = 0; i < 3; i++)
                {
                    var rect = new RectangleF(cx - r[i], cy - r[i], r[i] * 2, r[i] * 2);
                    using (var p = new Pen(A(45, cl[i]), 20)) g.DrawEllipse(p, rect);
                    using (var p = new Pen(cl[i], 20) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(p, rect, -90, Math.Max(2, 360 * Math.Min(1, v[i])));
                }
                using (var fv = F("Segoe UI Semibold", 36))
                    Render.Center(g, s.HasGpu ? (int)s.GpuTemp + "°" : DateTime.Now.ToString("HH:mm"), fv, Brushes.White, cx, cy - 26);
                using (var fl = F("Segoe UI", 12))
                using (var bl = B(Color.FromArgb(140, 150, 170)))
                    Render.Center(g, s.HasGpu ? "ГП" : "", fl, bl, cx, cy + 16);

                string[] lab = { "ЦП " + (int)Math.Round(s.CpuLoad) + "%", "ГП " + (int)s.GpuLoad + "%", "ОЗУ " + (int)Math.Round(s.RamLoad) + "%" };
                using (var f = F("Segoe UI Semibold", 13))
                using (var bt = B(Color.FromArgb(215, 222, 235)))
                {
                    float total = 0; var w = new float[3];
                    for (int i = 0; i < 3; i++) { w[i] = g.MeasureString(lab[i], f, PointF.Empty, StringFormat.GenericTypographic).Width + 16; total += w[i] + 14; }
                    float x = cx - (total - 14) / 2;
                    for (int i = 0; i < 3; i++)
                    {
                        using (var b = B(cl[i])) g.FillEllipse(b, x, 295, 9, 9);
                        g.DrawString(lab[i], f, bt, x + 14, 290, StringFormat.GenericTypographic);
                        x += w[i] + 14;
                    }
                }
            }
            return bmp;
        }

        // ---------- 6. Графики ----------
        static void Chart(Graphics g, RectangleF r, float[] data, int count, float lo, float hi, Color c, string title, string val)
        {
            using (var p = Render.RoundRect(r, 14)) using (var b = B(Color.FromArgb(18, 21, 31))) g.FillPath(b, p);
            using (var f = F("Segoe UI", 12)) using (var b = B(Color.FromArgb(140, 150, 170))) g.DrawString(title, f, b, r.X + 14, r.Y + 10);
            using (var f = F("Segoe UI Semibold", 26))
            {
                var sz = g.MeasureString(val, f, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString(val, f, Brushes.White, r.Right - 14 - sz.Width, r.Y + 6, StringFormat.GenericTypographic);
            }
            var plot = new RectangleF(r.X + 12, r.Y + 44, r.Width - 24, r.Height - 54);
            int n = data.Length, start = n - Math.Max(2, count);
            var pts = new PointF[n - start];
            for (int i = start; i < n; i++)
            {
                float t = (data[i] - lo) / (hi - lo);
                t = Math.Max(0, Math.Min(1, t));
                pts[i - start] = new PointF(plot.X + plot.Width * (i - (n - History.N)) / (History.N - 1), plot.Bottom - t * plot.Height);
            }
            if (pts.Length < 2) return;
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1], new PointF(pts[pts.Length - 1].X, plot.Bottom));
                path.AddLine(new PointF(pts[pts.Length - 1].X, plot.Bottom), new PointF(pts[0].X, plot.Bottom));
                path.CloseFigure();
                using (var lg = new LinearGradientBrush(plot, A(110, c), A(0, c), 90f)) g.FillPath(lg, path);
            }
            using (var p = new Pen(c, 2.2f) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
            var last = pts[pts.Length - 1];
            using (var b = B(c)) g.FillEllipse(b, last.X - 4, last.Y - 4, 8, 8);
        }

        static Bitmap Graphs(SensorData s, History h, Color a)
        {
            Graphics g; var bmp = New(out g, Color.FromArgb(8, 9, 14));
            using (g)
            {
                using (var f = F("Segoe UI Semibold", 15)) g.DrawString(DateTime.Now.ToString("HH:mm"), f, Brushes.White, 14, 8);
                using (var f = F("Segoe UI", 12)) using (var b = B(Color.FromArgb(120, 130, 150)))
                {
                    var t = "последняя минута";
                    var sz = g.MeasureString(t, f);
                    g.DrawString(t, f, b, S - 14 - sz.Width, 11);
                }
                float[] cpu, gt, gl; int cnt;
                lock (h) { cpu = (float[])h.Cpu.Clone(); gt = (float[])h.GpuTemp.Clone(); gl = (float[])h.GpuLoad.Clone(); cnt = h.Count; }
                if (s.HasGpu)
                    Chart(g, new RectangleF(10, 38, 300, 132), gt, cnt, 20, 100, Render.Heat(a, s.GpuHotspot), "Температура ГП", (int)s.GpuTemp + "°");
                else
                    Chart(g, new RectangleF(10, 38, 300, 132), gl, cnt, 0, 100, a, "Загрузка ГП", "—");
                Chart(g, new RectangleF(10, 178, 300, 132), cpu, cnt, 0, 100, HueShift(a, 120), "Загрузка ЦП", (int)Math.Round(s.CpuLoad) + "%");
            }
            return bmp;
        }

        // ---------- 7. Часы ----------
        static Bitmap Clock(SensorData s, Color a)
        {
            Graphics g; var bmp = New(out g, Color.FromArgb(8, 9, 13));
            using (g)
            {
                float cx = 160, cy = 160, r = 146;
                using (var lg = new LinearGradientBrush(new RectangleF(cx - r, cy - r, r * 2, r * 2), Color.FromArgb(26, 29, 40), Color.FromArgb(12, 13, 19), 90f))
                    g.FillEllipse(lg, cx - r, cy - r, r * 2, r * 2);
                using (var p = new Pen(A(90, a), 2)) g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
                using (var pM = new Pen(Color.FromArgb(225, 230, 240), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var pm = new Pen(Color.FromArgb(90, 98, 118), 1.6f))
                    for (int i = 0; i < 60; i++)
                    {
                        double ang = i * 6 * Math.PI / 180;
                        bool big = i % 5 == 0;
                        float r1 = r - 10, r2 = big ? r - 26 : r - 17;
                        g.DrawLine(big ? pM : pm, cx + (float)Math.Sin(ang) * r1, cy - (float)Math.Cos(ang) * r1, cx + (float)Math.Sin(ang) * r2, cy - (float)Math.Cos(ang) * r2);
                    }
                // маленькие циферблаты с датчиками
                SubDial(g, cx - 58, cy + 8, 32, s.CpuLoad / 100f, (int)Math.Round(s.CpuLoad) + "%", "ЦП", a);
                if (s.HasGpu) SubDial(g, cx + 58, cy + 8, 32, (s.GpuTemp - 20) / 80f, (int)s.GpuTemp + "°", "ГП", Render.Heat(a, s.GpuHotspot));
                else SubDial(g, cx + 58, cy + 8, 32, s.RamLoad / 100f, (int)Math.Round(s.RamLoad) + "%", "ОЗУ", a);
                using (var f = F("Segoe UI Semibold", 12)) using (var b = B(Color.FromArgb(150, 160, 180)))
                    Render.Center(g, DateTime.Now.ToString("d MMM", Ru).ToUpper(Ru), f, b, cx, cy - 62);

                var now = DateTime.Now;
                Hand(g, cx, cy, (now.Hour % 12 + now.Minute / 60f) * 30, 74, 7, Color.FromArgb(235, 238, 245));
                Hand(g, cx, cy, (now.Minute + now.Second / 60f) * 6, 112, 5, Color.FromArgb(235, 238, 245));
                Hand(g, cx, cy, now.Second * 6, 124, 2, a);
                using (var b = B(a)) g.FillEllipse(b, cx - 7, cy - 7, 14, 14);
                using (var b = B(Color.FromArgb(10, 11, 16))) g.FillEllipse(b, cx - 3, cy - 3, 6, 6);
            }
            return bmp;
        }

        static void Hand(Graphics g, float cx, float cy, float deg, float len, float w, Color c)
        {
            double a = deg * Math.PI / 180;
            using (var p = new Pen(Color.FromArgb(90, 0, 0, 0), w + 2) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(p, cx + 2, cy + 3, cx + 2 + (float)Math.Sin(a) * len, cy + 3 - (float)Math.Cos(a) * len);
            using (var p = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(p, cx - (float)Math.Sin(a) * 16, cy + (float)Math.Cos(a) * 16, cx + (float)Math.Sin(a) * len, cy - (float)Math.Cos(a) * len);
        }

        static void SubDial(Graphics g, float cx, float cy, float r, float frac, string val, string lab, Color c)
        {
            using (var p = new Pen(Color.FromArgb(40, 45, 60), 4)) g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            using (var p = new Pen(c, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, cx - r, cy - r, r * 2, r * 2, -90, 360 * Math.Max(0.02f, Math.Min(1, frac)));
            using (var f = F("Segoe UI Semibold", 16)) Render.Center(g, val, f, Brushes.White, cx, cy - 13);
            using (var f = F("Segoe UI", 10)) using (var b = B(c)) Render.Center(g, lab, f, b, cx, cy + 7);
        }

        // ---------- 8. Жидкость (анимация) ----------
        static Bitmap Liquid(SensorData s, Color a)
        {
            Graphics g; var bmp = New(out g, Color.FromArgb(6, 8, 13));
            using (g)
            {
                double t = (Environment.TickCount & int.MaxValue) / 1000.0;
                float cx = 160, cy = 160, r = 138;
                float val = MainTemp(s);
                float level = s.HasGpu ? (val - 20) / 75f : val / 100f;
                level = 0.12f + 0.8f * Math.Max(0, Math.Min(1, level));
                var c = s.HasGpu ? Render.Heat(a, s.GpuHotspot) : a;

                var circle = new RectangleF(cx - r, cy - r, r * 2, r * 2);
                using (var b = B(Color.FromArgb(14, 17, 26))) g.FillEllipse(b, circle);
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(circle);
                    var st = g.Save();
                    g.SetClip(clip);
                    float surface = cy + r - level * r * 2;
                    for (int layer = 0; layer < 2; layer++)
                    {
                        var pts = new System.Collections.Generic.List<PointF>();
                        for (int x = -4; x <= S + 4; x += 6)
                        {
                            double ph = layer == 0 ? t * 2.1 : -t * 1.6 + 1.7;
                            float amp = layer == 0 ? 7 : 5;
                            pts.Add(new PointF(x, surface + (float)(Math.Sin(x / 34.0 + ph) * amp + Math.Sin(x / 17.0 + ph * 1.3) * amp * 0.3) + layer * 4));
                        }
                        pts.Add(new PointF(S + 4, S + 4)); pts.Add(new PointF(-4, S + 4));
                        using (var path = new GraphicsPath())
                        {
                            path.AddPolygon(pts.ToArray());
                            var top = layer == 0 ? A(120, c) : A(235, Render.Mix(c, Color.Black, 0.35f));
                            using (var lg = new LinearGradientBrush(new RectangleF(0, surface - 10, S, S - surface + 20), top, A(255, Render.Mix(c, Color.Black, 0.75f)), 90f))
                                g.FillPath(lg, path);
                        }
                    }
                    // пузырьки
                    var rnd = new Random(7);
                    using (var bp = new Pen(A(110, Color.White), 1.3f))
                        for (int i = 0; i < 14; i++)
                        {
                            float bx = cx - r + 20 + (float)rnd.NextDouble() * (r * 2 - 40);
                            float speed = 18 + (float)rnd.NextDouble() * 30, sz = 3 + (float)rnd.NextDouble() * 6;
                            float span = cy + r - surface;
                            if (span < 20) continue;
                            float by = cy + r - (float)((t * speed + rnd.NextDouble() * 500) % span);
                            bx += (float)Math.Sin(t * 2 + i) * 4;
                            g.DrawEllipse(bp, bx, by, sz, sz);
                        }
                    g.Restore(st);
                }
                // стекло
                using (var p = new Pen(A(120, c), 3)) g.DrawEllipse(p, circle);
                using (var p = new Pen(A(40, Color.White), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, cx - r + 14, cy - r + 14, r * 2 - 28, r * 2 - 28, 200, 50);

                using (var path = new GraphicsPath())
                using (var ff = new FontFamily("Segoe UI Semibold"))
                {
                    string txt = ((int)val) + (s.HasGpu ? "°" : "%");
                    path.AddString(txt, ff, 0, 72, PointF.Empty, StringFormat.GenericTypographic);
                    var bb = path.GetBounds();
                    using (var m = new Matrix()) { m.Translate(cx - bb.Width / 2 - bb.X, cy - 56 - bb.Y); path.Transform(m); }
                    using (var p = new Pen(Color.FromArgb(120, 0, 0, 0), 6) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
                    g.FillPath(Brushes.White, path);
                }
                using (var f = F("Segoe UI Semibold", 14))
                    Render.Center(g, s.HasGpu ? "ГП  ·  ЦП " + (int)Math.Round(s.CpuLoad) + "%" : "загрузка ЦП", f, Brushes.White, cx, cy + 30);
            }
            return bmp;
        }
    }
}
