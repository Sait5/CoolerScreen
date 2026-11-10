using System;
using System.IO;

namespace CoolerScreen
{
    // Кадр в формате YUV 4:2:0
    public sealed class Yuv
    {
        public readonly int W, H;
        public readonly byte[] Y, U, V;
        public Yuv(int w, int h) { W = w; H = h; Y = new byte[w * h]; U = new byte[w * h / 4]; V = new byte[w * h / 4]; }

        static byte C(int v) { return (byte)(v < 1 ? 1 : v > 254 ? 254 : v); }

        // Поворот на rot*90° по часовой (кадр квадратный)
        public Yuv Rotate(int rot, Yuv dst)
        {
            rot &= 3;
            if (rot == 0) return this;
            if (dst == null || dst.W != W) dst = new Yuv(W, H);
            RotPlane(Y, dst.Y, W, rot); RotPlane(U, dst.U, W / 2, rot); RotPlane(V, dst.V, W / 2, rot);
            return dst;
        }

        static void RotPlane(byte[] s, byte[] d, int n, int rot)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int dx, dy;
                    if (rot == 1) { dx = n - 1 - y; dy = x; }
                    else if (rot == 2) { dx = n - 1 - x; dy = n - 1 - y; }
                    else { dx = y; dy = n - 1 - x; }
                    d[dy * n + dx] = s[y * n + x];
                }
        }

        // bgra: 32bpp BGRA, stride = w*4 (BT.601, limited range)
        public static Yuv FromBgra(byte[] bgra, int w, int h)
        {
            var f = new Yuv(w, h);
            for (int j = 0; j < h; j++)
            {
                int row = j * w * 4, yr = j * w;
                for (int i = 0; i < w; i++)
                {
                    int p = row + i * 4;
                    f.Y[yr + i] = C(((66 * bgra[p + 2] + 129 * bgra[p + 1] + 25 * bgra[p] + 128) >> 8) + 16);
                }
            }
            int cw = w / 2;
            for (int j = 0; j < h / 2; j++)
                for (int i = 0; i < cw; i++)
                {
                    int p0 = ((j * 2) * w + i * 2) * 4, p1 = p0 + w * 4;
                    int b = bgra[p0] + bgra[p0 + 4] + bgra[p1] + bgra[p1 + 4];
                    int g = bgra[p0 + 1] + bgra[p0 + 5] + bgra[p1 + 1] + bgra[p1 + 5];
                    int r = bgra[p0 + 2] + bgra[p0 + 6] + bgra[p1 + 2] + bgra[p1 + 6];
                    r >>= 2; g >>= 2; b >>= 2;
                    f.U[j * cw + i] = C(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    f.V[j * cw + i] = C(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            return f;
        }
    }

    // Мини-кодировщик H.264 без сжатия: IDR из I_PCM-макроблоков, а P-кадры
    // передают только изменившиеся макроблоки (остальные — skip). Ноль зависимостей, почти ноль CPU.
    public sealed class H264Pcm
    {
        readonly int w, h, mbw, mbh;
        int idrId;
        readonly byte[] sps, pps;
        Yuv prev;

        public H264Pcm(int width, int height)
        {
            w = width; h = height; mbw = w / 16; mbh = h / 16;

            var b = new BitWriter();
            b.U(8, 66); b.U(8, 0xC0); b.U(8, 30);   // Constrained Baseline, level 3.0
            b.UE(0);            // sps id
            b.UE(0);            // log2_max_frame_num-4  -> frame_num по модулю 16
            b.UE(2);            // poc type 2
            b.UE(1);            // max refs
            b.U(1, 0);
            b.UE(mbw - 1); b.UE(mbh - 1);
            b.U(1, 1); b.U(1, 1); b.U(1, 0); b.U(1, 0);
            b.Trailing();
            sps = Nal(0x67, b.ToArray());

            b = new BitWriter();
            b.UE(0); b.UE(0);
            b.U(1, 0); b.U(1, 0);
            b.UE(0); b.UE(0); b.UE(0);
            b.U(1, 0); b.U(2, 0);
            b.SE(0); b.SE(0); b.SE(0);
            b.U(1, 1);          // deblocking control present
            b.U(1, 0); b.U(1, 0);
            b.Trailing();
            pps = Nal(0x68, b.ToArray());
        }

        public bool HasReference { get { return prev != null; } }

        public bool SameAsPrev(Yuv f)
        {
            if (prev == null) return false;
            return Eq(prev.Y, f.Y) && Eq(prev.U, f.U) && Eq(prev.V, f.V);
        }
        static bool Eq(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        public void Reset() { prev = null; }

        static byte[] Nal(byte header, byte[] rbsp)
        {
            var ms = new MemoryStream(rbsp.Length + rbsp.Length / 64 + 8);
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1); ms.WriteByte(header);
            int zeros = 0;
            foreach (byte x in rbsp)
            {
                if (zeros >= 2 && x <= 3) { ms.WriteByte(3); zeros = 0; }
                ms.WriteByte(x);
                zeros = x == 0 ? zeros + 1 : 0;
            }
            return ms.ToArray();
        }

        void Mb(BitWriter b, Yuv f, int mx, int my)
        {
            int cw = w / 2;
            b.Align();
            for (int y = 0; y < 16; y++) b.Bytes(f.Y, (my * 16 + y) * w + mx * 16, 16);
            for (int y = 0; y < 8; y++) b.Bytes(f.U, (my * 8 + y) * cw + mx * 8, 8);
            for (int y = 0; y < 8; y++) b.Bytes(f.V, (my * 8 + y) * cw + mx * 8, 8);
        }

        bool Same(Yuv a, Yuv c, int mx, int my)
        {
            int cw = w / 2;
            for (int y = 0; y < 16; y++)
            {
                int o = (my * 16 + y) * w + mx * 16;
                for (int i = 0; i < 16; i++) if (a.Y[o + i] != c.Y[o + i]) return false;
            }
            for (int y = 0; y < 8; y++)
            {
                int o = (my * 8 + y) * cw + mx * 8;
                for (int i = 0; i < 8; i++) if (a.U[o + i] != c.U[o + i] || a.V[o + i] != c.V[o + i]) return false;
            }
            return true;
        }

        static void CopyMb(Yuv dst, Yuv src, int mx, int my)
        {
            int w = dst.W, cw = w / 2;
            for (int y = 0; y < 16; y++) Buffer.BlockCopy(src.Y, (my * 16 + y) * w + mx * 16, dst.Y, (my * 16 + y) * w + mx * 16, 16);
            for (int y = 0; y < 8; y++)
            {
                int o = (my * 8 + y) * cw + mx * 8;
                Buffer.BlockCopy(src.U, o, dst.U, o, 8);
                Buffer.BlockCopy(src.V, o, dst.V, o, 8);
            }
        }

        // Самостоятельный ключевой кадр (SPS + PPS + IDR). frame_num = 0.
        public byte[] Idr(Yuv f)
        {
            var b = new BitWriter(w * h * 2);
            b.UE(0); b.UE(7); b.UE(0);
            b.U(4, 0);
            b.UE(idrId); idrId ^= 1;
            b.U(1, 0); b.U(1, 0);
            b.SE(0);
            b.UE(1);            // deblocking off
            for (int my = 0; my < mbh; my++)
                for (int mx = 0; mx < mbw; mx++) { b.UE(25); Mb(b, f, mx, my); }
            b.Trailing();
            byte[] slice = Nal(0x65, b.ToArray());

            prev = new Yuv(w, h);
            Buffer.BlockCopy(f.Y, 0, prev.Y, 0, f.Y.Length);
            Buffer.BlockCopy(f.U, 0, prev.U, 0, f.U.Length);
            Buffer.BlockCopy(f.V, 0, prev.V, 0, f.V.Length);

            var outp = new byte[sps.Length + pps.Length + slice.Length];
            Buffer.BlockCopy(sps, 0, outp, 0, sps.Length);
            Buffer.BlockCopy(pps, 0, outp, sps.Length, pps.Length);
            Buffer.BlockCopy(slice, 0, outp, sps.Length + pps.Length, slice.Length);
            return outp;
        }

        // P-кадр: передаём только отличающиеся макроблоки. f == null -> пустой кадр (keepalive).
        public byte[] P(Yuv f, int frameNum, out int changed)
        {
            changed = 0;
            var b = new BitWriter(4096);
            b.UE(0); b.UE(5); b.UE(0);
            b.U(4, frameNum & 15);
            b.U(1, 0);          // num_ref_idx_override
            b.U(1, 0);          // ref_pic_list_modification
            b.U(1, 0);          // adaptive_ref_pic_marking
            b.SE(0);
            b.UE(1);
            int skip = 0;
            for (int my = 0; my < mbh; my++)
                for (int mx = 0; mx < mbw; mx++)
                {
                    if (f == null || Same(prev, f, mx, my)) { skip++; continue; }
                    b.UE(skip); skip = 0;
                    b.UE(30);   // I_PCM внутри P-слайса
                    Mb(b, f, mx, my);
                    CopyMb(prev, f, mx, my);
                    changed++;
                }
            if (skip > 0) b.UE(skip);
            b.Trailing();
            return Nal(0x41, b.ToArray());
        }

        // Переписать frame_num у заранее закодированного P-кадра (биты 7..10 полезной нагрузки).
        public static void PatchFrameNum(byte[] pkt, int fn)
        {
            fn &= 15;
            pkt[5] = (byte)((pkt[5] & 0xFE) | (fn >> 3));
            pkt[6] = (byte)((pkt[6] & 0x1F) | ((fn & 7) << 5));
        }
    }

    sealed class BitWriter
    {
        byte[] buf; int pos; int cur; int nbits;
        public BitWriter(int cap = 64) { buf = new byte[cap]; }
        void Put(byte x) { if (pos == buf.Length) Array.Resize(ref buf, buf.Length * 2); buf[pos++] = x; }
        public void U(int n, int v)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                cur = (cur << 1) | ((v >> i) & 1);
                if (++nbits == 8) { Put((byte)cur); cur = 0; nbits = 0; }
            }
        }
        public void UE(int v)
        {
            int x = v + 1, len = 0;
            for (int t = x; t > 1; t >>= 1) len++;
            U(len, 0); U(len + 1, x);
        }
        public void SE(int v) { UE(v <= 0 ? -2 * v : 2 * v - 1); }
        public void Align() { while (nbits != 0) U(1, 0); }
        public void Bytes(byte[] src, int off, int n)
        {
            if (pos + n > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, pos + n));
            Buffer.BlockCopy(src, off, buf, pos, n); pos += n;
        }
        public void Trailing() { U(1, 1); Align(); }
        public byte[] ToArray() { var r = new byte[pos]; Buffer.BlockCopy(buf, 0, r, 0, pos); return r; }
    }
}
