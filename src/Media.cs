using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace CoolerScreen
{
    // Картинка или GIF, заранее подогнанная под 320x320 и переведённая в YUV
    public sealed class MediaClip
    {
        public readonly List<Yuv> Frames = new List<Yuv>();
        public readonly List<int> Delays = new List<int>();
        public byte[][] Packets;     // сжатые кадры (кеш)
        public int PacketsRot;
        public bool Animated { get { return Frames.Count > 1; } }
        const int MaxFrames = 600;

        public static MediaClip Load(string path, Render.Fit fit)
        {
            var clip = new MediaClip();
            using (var ms = new MemoryStream(File.ReadAllBytes(path)))
            using (var img = Image.FromStream(ms))
            using (var bmp = new Bitmap(Render.S, Render.S, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                int count = 1;
                try { count = img.GetFrameCount(FrameDimension.Time); } catch { }
                int[] delays = null;
                if (count > 1)
                {
                    try
                    {
                        var pi = img.GetPropertyItem(0x5100);
                        delays = new int[count];
                        for (int i = 0; i < count && i * 4 + 3 < pi.Value.Length; i++)
                            delays[i] = BitConverter.ToInt32(pi.Value, i * 4) * 10;
                    }
                    catch { }
                }
                count = Math.Min(count, MaxFrames);
                for (int i = 0; i < count; i++)
                {
                    if (count > 1) img.SelectActiveFrame(FrameDimension.Time, i);
                    Render.DrawFitted(g, img, fit);
                    clip.Frames.Add(Yuv.FromBgra(Render.Bgra(bmp), Render.S, Render.S));
                    int d = delays != null ? delays[i] : 100;
                    clip.Delays.Add(d <= 10 ? 100 : Math.Max(20, d));
                }
            }
            return clip;
        }
    }

    // Полупрозрачный слой (датчики), смешивается прямо в YUV
    public sealed class OverlayYuv
    {
        readonly Yuv col; readonly byte[] aY, aC; readonly int y0;

        public OverlayYuv(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var bgra = Render.Bgra(bmp);
            col = Yuv.FromBgra(bgra, w, h);
            aY = new byte[w * h]; aC = new byte[w * h / 4];
            int first = h;
            for (int i = 0; i < w * h; i++) { aY[i] = bgra[i * 4 + 3]; if (aY[i] != 0 && i / w < first) first = i / w; }
            y0 = first & ~1;
            int cw = w / 2;
            for (int j = 0; j < h / 2; j++)
                for (int i = 0; i < cw; i++)
                {
                    int p = j * 2 * w + i * 2;
                    aC[j * cw + i] = (byte)((aY[p] + aY[p + 1] + aY[p + w] + aY[p + w + 1]) >> 2);
                }
        }

        public void BlendInto(Yuv dst)
        {
            int w = dst.W, h = dst.H, cw = w / 2;
            for (int i = y0 * w; i < w * h; i++)
            {
                int a = aY[i]; if (a == 0) continue;
                dst.Y[i] = (byte)((dst.Y[i] * (255 - a) + col.Y[i] * a + 127) / 255);
            }
            for (int i = (y0 / 2) * cw; i < cw * (h / 2); i++)
            {
                int a = aC[i]; if (a == 0) continue;
                dst.U[i] = (byte)((dst.U[i] * (255 - a) + col.U[i] * a + 127) / 255);
                dst.V[i] = (byte)((dst.V[i] * (255 - a) + col.V[i] * a + 127) / 255);
            }
        }
    }
}
