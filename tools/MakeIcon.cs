using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Рисует иконку программы (экран помпы с кольцом-датчиком и каплей) во всех размерах и пишет .ico
static class MakeIcon
{
    static GraphicsPath Squircle(float x, float y, float s, float r)
    {
        var p = new GraphicsPath(); float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + s - d, y, d, d, 270, 90);
        p.AddArc(x + s - d, y + s - d, d, d, 0, 90); p.AddArc(x, y + s - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    static Bitmap Draw(int n)
    {
        var b = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = n / 256f;
            Color c1 = Color.FromArgb(0, 210, 255), c2 = Color.FromArgb(150, 90, 255);

            // корпус
            using (var body = Squircle(8 * k, 8 * k, 240 * k, 58 * k))
            {
                using (var lg = new LinearGradientBrush(new RectangleF(0, 0, n, n), Color.FromArgb(30, 34, 52), Color.FromArgb(9, 10, 16), 90f))
                    g.FillPath(lg, body);
                if (n >= 32)
                    using (var p = new Pen(Color.FromArgb(70, 120, 140, 200), Math.Max(1, 3 * k))) g.DrawPath(p, body);
            }

            // свечение
            if (n >= 48)
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(28 * k, 28 * k, 200 * k, 200 * k);
                    using (var pg = new PathGradientBrush(path) { CenterColor = Color.FromArgb(70, c1), SurroundColors = new[] { Color.FromArgb(0, c1) } })
                        g.FillPath(pg, path);
                }

            // экран помпы
            float cx = 128 * k, cy = 128 * k, r = 84 * k;
            using (var b2 = new SolidBrush(Color.FromArgb(10, 12, 20))) g.FillEllipse(b2, cx - r, cy - r, r * 2, r * 2);

            // кольцо-датчик с градиентом
            float rw = Math.Max(2.5f, 22 * k), rr = r - rw / 2 - 4 * k;
            var rect = new RectangleF(cx - rr, cy - rr, rr * 2, rr * 2);
            using (var p = new Pen(Color.FromArgb(40, 46, 66), rw)) g.DrawArc(p, rect, 135, 270);
            using (var lg = new LinearGradientBrush(new RectangleF(cx - r, cy - r, r * 2, r * 2), c1, c2, 0f))
            using (var p = new Pen(lg, rw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(p, rect, 135, 200);

            // капля
            float dh = 70 * k, dw = 48 * k, dx = cx, dy = cy - dh / 2 + 4 * k;
            using (var drop = new GraphicsPath())
            {
                drop.AddBezier(dx, dy, dx + dw * 0.15f, dy + dh * 0.25f, dx + dw * 0.5f, dy + dh * 0.45f, dx + dw * 0.5f, dy + dh * 0.68f);
                drop.AddArc(dx - dw / 2, dy + dh - dw, dw, dw, 0, 180);
                drop.AddBezier(dx - dw * 0.5f, dy + dh * 0.68f, dx - dw * 0.5f, dy + dh * 0.45f, dx - dw * 0.15f, dy + dh * 0.25f, dx, dy);
                drop.CloseFigure();
                using (var lg = new LinearGradientBrush(new RectangleF(dx - dw / 2, dy, dw, dh), Color.FromArgb(120, 235, 255), c1, 90f))
                    g.FillPath(lg, drop);
                if (n >= 48)
                    using (var hl = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                        g.FillEllipse(hl, dx - dw * 0.28f, dy + dh * 0.55f, dw * 0.18f, dh * 0.2f);
            }
        }
        return b;
    }

    static void Main(string[] a)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
            using (var bmp = Draw(s))
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
        using (var bmp = Draw(256)) bmp.Save(Path.ChangeExtension(a[0], ".png"), ImageFormat.Png);

        using (var f = new BinaryWriter(File.Create(a[0])))
        {
            f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
            int off = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32);
                f.Write(pngs[i].Length); f.Write(off);
                off += pngs[i].Length;
            }
            foreach (var p in pngs) f.Write(p);
        }
    }
}
