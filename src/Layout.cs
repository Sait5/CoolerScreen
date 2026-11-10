using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace CoolerScreen
{
    public enum WKind { Clock, Date, Text, Cpu, GpuTemp, GpuHot, GpuLoad, Ram, GpuPower, GpuClock, GpuFan }
    public enum WStyle { Number, Ring, Bar }

    // Блок своего дашборда
    public sealed class Widget
    {
        public WKind Kind;
        public WStyle Style = WStyle.Number;
        public float X = 160, Y = 160, Scale = 1;
        public Color? Color;
        public string Text = "Мой ПК";
        public RectangleF Bounds;   // заполняется при отрисовке

        public bool IsSensor { get { return Kind >= WKind.Cpu; } }
        public Widget Clone() { return (Widget)MemberwiseClone(); }

        public static readonly string[] KindNames = { "Часы", "Дата", "Текст", "Загрузка ЦП", "Темп. ГП", "Хотспот ГП", "Загрузка ГП", "ОЗУ", "Мощность ГП", "Частота ГП", "Кулер ГП" };
    }

    public sealed class CustomLayout
    {
        public readonly List<Widget> Items = new List<Widget>();
        static readonly CultureInfo Ru = new CultureInfo("ru-RU");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public CustomLayout Clone()
        {
            var c = new CustomLayout();
            foreach (var w in Items) c.Items.Add(w.Clone());
            return c;
        }

        public static CustomLayout Default()
        {
            var l = new CustomLayout();
            l.Items.Add(new Widget { Kind = WKind.Clock, X = 160, Y = 52, Scale = 1.1f });
            l.Items.Add(new Widget { Kind = WKind.Date, X = 160, Y = 98 });
            l.Items.Add(new Widget { Kind = WKind.GpuTemp, Style = WStyle.Ring, X = 88, Y = 182 });
            l.Items.Add(new Widget { Kind = WKind.Cpu, Style = WStyle.Ring, X = 232, Y = 182 });
            l.Items.Add(new Widget { Kind = WKind.Ram, Style = WStyle.Bar, X = 160, Y = 278, Scale = 1.4f });
            return l;
        }

        public static CustomLayout Load()
        {
            try
            {
                if (!File.Exists(Settings.LayoutPath)) return Default();
                var l = new CustomLayout();
                foreach (var line in File.ReadAllLines(Settings.LayoutPath, Encoding.UTF8))
                {
                    var p = line.Split('|');
                    if (p.Length < 7) continue;
                    var w = new Widget();
                    WKind k; WStyle st;
                    if (!Enum.TryParse(p[0], out k)) continue;
                    w.Kind = k;
                    if (Enum.TryParse(p[1], out st)) w.Style = st;
                    w.X = float.Parse(p[2], Inv); w.Y = float.Parse(p[3], Inv); w.Scale = float.Parse(p[4], Inv);
                    if (p[5] != "-") w.Color = ColorTranslator.FromHtml(p[5]);
                    w.Text = Encoding.UTF8.GetString(Convert.FromBase64String(p[6]));
                    l.Items.Add(w);
                }
                return l;
            }
            catch { return Default(); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var sb = new StringBuilder();
                foreach (var w in Items)
                    sb.Append(w.Kind).Append('|').Append(w.Style).Append('|')
                      .Append(w.X.ToString("0.#", Inv)).Append('|').Append(w.Y.ToString("0.#", Inv)).Append('|').Append(w.Scale.ToString("0.##", Inv)).Append('|')
                      .Append(w.Color.HasValue ? Settings.Hex(w.Color.Value) : "-").Append('|')
                      .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(w.Text ?? ""))).Append("\r\n");
                File.WriteAllText(Settings.LayoutPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static void Value(WKind k, SensorData s, out string label, out string val, out string unit, out float frac, out bool hot)
        {
            hot = false;
            switch (k)
            {
                case WKind.Cpu: label = "ЦП"; val = ((int)Math.Round(s.CpuLoad)).ToString(); unit = "%"; frac = s.CpuLoad / 100; break;
                case WKind.GpuTemp: label = "ГП"; val = ((int)s.GpuTemp).ToString(); unit = "°C"; frac = (s.GpuTemp - 20) / 80; hot = true; break;
                case WKind.GpuHot: label = "хотспот"; val = ((int)s.GpuHotspot).ToString(); unit = "°C"; frac = (s.GpuHotspot - 20) / 90; hot = true; break;
                case WKind.GpuLoad: label = "загрузка ГП"; val = ((int)s.GpuLoad).ToString(); unit = "%"; frac = s.GpuLoad / 100; break;
                case WKind.Ram: label = "ОЗУ"; val = s.RamUsedGb.ToString("0.0", Ru); unit = " ГБ"; frac = s.RamLoad / 100; break;
                case WKind.GpuPower: label = "мощность ГП"; val = ((int)s.GpuPower).ToString(); unit = " Вт"; frac = s.GpuPower / 220; break;
                case WKind.GpuClock: label = "частота ГП"; val = ((int)s.GpuClock).ToString(); unit = " МГц"; frac = s.GpuClock / 2800; break;
                case WKind.GpuFan: label = "вентилятор"; val = ((int)s.GpuFan).ToString(); unit = " об/м"; frac = s.GpuFan / 3000; break;
                default: label = ""; val = ""; unit = ""; frac = 0; break;
            }
            frac = Math.Max(0, Math.Min(1, frac));
        }

        static Font F(string fam, float px) { return new Font(fam, Math.Max(4, px), GraphicsUnit.Pixel); }

        static readonly object bgLock = new object();
        static string bgPath; static Bitmap bgCache;

        public static void DrawBackground(Graphics g, string bg, Color accent)
        {
            int S = CoolerScreen.Render.S;
            if (!string.IsNullOrEmpty(bg) && bg.StartsWith("#"))
            {
                try { using (var b = new SolidBrush(ColorTranslator.FromHtml(bg))) g.FillRectangle(b, 0, 0, S, S); return; } catch { }
            }
            if (!string.IsNullOrEmpty(bg) && File.Exists(bg))
            {
                lock (bgLock)
                {
                    if (bgPath != bg)
                    {
                        if (bgCache != null) bgCache.Dispose();
                        bgCache = null; bgPath = bg;
                        try
                        {
                            using (var ms = new MemoryStream(File.ReadAllBytes(bg)))
                            using (var img = Image.FromStream(ms))
                            {
                                bgCache = new Bitmap(S, S, PixelFormat.Format32bppArgb);
                                using (var bg2 = Graphics.FromImage(bgCache)) CoolerScreen.Render.DrawFitted(bg2, img, CoolerScreen.Render.Fit.Cover);
                            }
                        }
                        catch { bgCache = null; }
                    }
                    if (bgCache != null) { g.DrawImageUnscaled(bgCache, 0, 0); return; }
                }
            }
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, S, S), System.Drawing.Color.FromArgb(8, 10, 17), System.Drawing.Color.FromArgb(14, 17, 30), 90f))
                g.FillRectangle(lg, 0, 0, S, S);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(-60, -150, S + 120, 260);
                using (var pg = new PathGradientBrush(path) { CenterColor = System.Drawing.Color.FromArgb(50, accent), SurroundColors = new[] { System.Drawing.Color.FromArgb(0, accent) } })
                    g.FillPath(pg, path);
            }
        }

        // Рисует все блоки; фон кешируется снаружи
        public Bitmap Render(SensorData s, Color accent, string bg)
        {
            var bmp = new Bitmap(CoolerScreen.Render.S, CoolerScreen.Render.S, PixelFormat.Format32bppArgb);
            using (var g = CoolerScreen.Render.G(bmp))
            {
                DrawBackground(g, bg, accent);
                foreach (var w in Items) DrawWidget(g, w, s, accent);
            }
            return bmp;
        }

        public static void DrawWidget(Graphics g, Widget w, SensorData s, Color accent)
        {
            float k = w.Scale;
            Color c = w.Color ?? accent;
            var sf = StringFormat.GenericTypographic;
            if (!w.IsSensor)
            {
                string t; Font f; Brush b;
                if (w.Kind == WKind.Clock) { t = DateTime.Now.ToString("HH:mm"); f = F("Segoe UI Light", 52 * k); }
                else if (w.Kind == WKind.Date) { t = DateTime.Now.ToString("dddd, d MMMM", Ru); f = F("Segoe UI", 14 * k); }
                else { t = string.IsNullOrEmpty(w.Text) ? " " : w.Text; f = F("Segoe UI Semibold", 22 * k); }
                using (f)
                using (b = new SolidBrush(w.Kind == WKind.Date && !w.Color.HasValue ? System.Drawing.Color.FromArgb(150, 160, 185) : w.Kind == WKind.Clock && !w.Color.HasValue ? System.Drawing.Color.White : c))
                {
                    var sz = g.MeasureString(t, f, PointF.Empty, sf);
                    w.Bounds = new RectangleF(w.X - sz.Width / 2, w.Y - sz.Height / 2, sz.Width, sz.Height);
                    g.DrawString(t, f, b, w.Bounds.X, w.Bounds.Y, sf);
                }
                return;
            }

            string label, val, unit; float frac; bool hot;
            Value(w.Kind, s, out label, out val, out unit, out frac, out hot);
            if (hot && !w.Color.HasValue) c = CoolerScreen.Render.Heat(accent, s.GpuHotspot);

            if (w.Style == WStyle.Ring)
            {
                float r = 46 * k;
                w.Bounds = new RectangleF(w.X - r - 8 * k, w.Y - r - 8 * k, r * 2 + 16 * k, r * 2 + 16 * k);
                var rect = new RectangleF(w.X - r, w.Y - r, r * 2, r * 2);
                using (var p = new Pen(System.Drawing.Color.FromArgb(34, 40, 56), 9 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, rect, 135, 270);
                using (var p = new Pen(c, 9 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, rect, 135, 270 * Math.Max(0.01f, frac));
                using (var fv = F("Segoe UI Semibold", 24 * k))
                using (var fu = F("Segoe UI", 12 * k))
                using (var ub = new SolidBrush(System.Drawing.Color.FromArgb(150, 160, 185)))
                {
                    var vs = g.MeasureString(val, fv, PointF.Empty, sf); var us = g.MeasureString(unit.Trim(), fu, PointF.Empty, sf);
                    float x = w.X - (vs.Width + us.Width + 2) / 2;
                    g.DrawString(val, fv, Brushes.White, x, w.Y - 20 * k, sf);
                    g.DrawString(unit.Trim(), fu, ub, x + vs.Width + 2, w.Y - 10 * k, sf);
                }
                using (var fl = F("Segoe UI Semibold", 11 * k)) using (var lb = new SolidBrush(c))
                    CoolerScreen.Render.Center(g, label, fl, lb, w.X, w.Y + 10 * k);
            }
            else if (w.Style == WStyle.Bar)
            {
                float bw = 110 * k, h = 44 * k, x = w.X - bw / 2, y = w.Y - h / 2;
                w.Bounds = new RectangleF(x - 4, y - 4, bw + 8, h + 8);
                using (var fl = F("Segoe UI", 11 * k)) using (var lb = new SolidBrush(System.Drawing.Color.FromArgb(135, 145, 170)))
                    g.DrawString(label, fl, lb, x, y, sf);
                using (var fv = F("Segoe UI Semibold", 15 * k))
                    g.DrawString(val + unit, fv, Brushes.White, x, y + 14 * k, sf);
                using (var tb = new SolidBrush(System.Drawing.Color.FromArgb(34, 40, 56))) g.FillRectangle(tb, x, y + 37 * k, bw, 4 * k);
                using (var cb = new SolidBrush(c)) g.FillRectangle(cb, x, y + 37 * k, bw * Math.Max(0.02f, frac), 4 * k);
            }
            else
            {
                using (var fl = F("Segoe UI Semibold", 12 * k))
                using (var fv = F("Segoe UI Semibold", 34 * k))
                using (var fu = F("Segoe UI", 15 * k))
                using (var lb = new SolidBrush(c))
                using (var ub = new SolidBrush(System.Drawing.Color.FromArgb(150, 160, 185)))
                {
                    var ls = g.MeasureString(label, fl, PointF.Empty, sf);
                    var vs = g.MeasureString(val, fv, PointF.Empty, sf);
                    var us = g.MeasureString(unit, fu, PointF.Empty, sf);
                    float tw = vs.Width + us.Width, th = ls.Height + vs.Height;
                    float w0 = Math.Max(tw, ls.Width);
                    w.Bounds = new RectangleF(w.X - w0 / 2 - 4, w.Y - th / 2 - 4, w0 + 8, th + 8);
                    g.DrawString(label, fl, lb, w.X - ls.Width / 2, w.Y - th / 2, sf);
                    float x = w.X - tw / 2, y = w.Y - th / 2 + ls.Height;
                    g.DrawString(val, fv, Brushes.White, x, y, sf);
                    g.DrawString(unit, fu, ub, x + vs.Width, y + vs.Height - us.Height - 3 * k, sf);
                }
            }
        }

        public Widget HitTest(PointF p)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var b = Items[i].Bounds; b.Inflate(4, 4);
                if (b.Contains(p)) return Items[i];
            }
            return null;
        }
    }
}
