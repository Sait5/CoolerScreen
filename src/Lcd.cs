using System;
using System.IO.Ports;
using System.Management;
using System.Threading;

namespace CoolerScreen
{
    // Экран водянки: Allwinner USB CDC (VID 1F3A / PID 0007), протокол 5A A5 <type> 00 | len LE | payload
    public sealed class Lcd : IDisposable
    {
        const byte T_BRIGHT = 0x80, T_HELLO = 0x81, T_VIDEO = 0x85, T_RES = 0x90;
        SerialPort port;
        public string PortName { get; private set; }
        public int Width = 320, Height = 320;
        // Семейство Smart Screen V28 (Jungle Leopard Astroshel и совместимые водянки на том же чипе)
        public string Model = "Smart Screen V28";

        public static string FindPort()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_1F3A&PID_0007%'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string name = (o["Name"] as string) ?? "";
                        int a = name.LastIndexOf("(COM");
                        if (a >= 0) { int e = name.IndexOf(')', a); return name.Substring(a + 1, e - a - 1); }
                    }
            }
            catch { }
            return null;
        }

        public bool Answered { get; private set; }

        public static Lcd Open(int brightnessPercent, Action<string> status)
        {
            string name = FindPort();
            if (name == null) return null;
            var p = new SerialPort(name, 1000000, Parity.None, 8, StopBits.One);
            p.DtrEnable = false; p.RtsEnable = false;   // иначе панель остаётся чёрной
            p.ReadTimeout = 500;
            p.WriteTimeout = 15000;   // кадр нельзя обрывать на середине — экран зависнет, ожидая остаток
            p.WriteBufferSize = 1 << 20;
            p.Open();   // если порт занят другой программой — UnauthorizedAccessException
            var lcd = new Lcd { port = p, PortName = name };
            lcd.Answered = lcd.Handshake();
            if (!lcd.Answered)
            {
                // Экран молчит: скорее всего, он «застрял», дожидаясь остатка оборванного кадра.
                // Досылаем ему пустые байты, пока не проглотит, — это возвращает его в чувство без выключения ПК.
                if (status != null) status("Экран не отвечает — пробую оживить…");
                lcd.Answered = lcd.Recover();
            }
            lcd.SetBrightness(brightnessPercent);
            return lcd;
        }

        bool Recover()
        {
            var zeros = new byte[256 * 1024];
            const int maxMb = 40;
            for (int mb = 0; mb < maxMb; mb++)
            {
                port.Write(zeros, 0, zeros.Length); port.Write(zeros, 0, zeros.Length);
                port.Write(zeros, 0, zeros.Length); port.Write(zeros, 0, zeros.Length);
                if (mb % 2 == 1 && Handshake()) return true;
            }
            return Handshake();
        }

        void Write(byte type, byte[] payload)
        {
            var hdr = new byte[8] { 0x5A, 0xA5, type, 0, 0, 0, 0, 0 };
            BitConverter.GetBytes(payload.Length).CopyTo(hdr, 4);
            port.Write(hdr, 0, 8);
            port.Write(payload, 0, payload.Length);
        }

        byte[] ReadReply()
        {
            var buf = new byte[256]; int n = 0;
            var until = DateTime.UtcNow.AddMilliseconds(600);
            while (DateTime.UtcNow < until && n < buf.Length)
            {
                try { n += port.Read(buf, n, buf.Length - n); } catch (TimeoutException) { break; }
                if (n >= 8 && buf[0] == 0x5A && buf[1] == 0xA5)
                {
                    int le = BitConverter.ToInt32(buf, 4), be = (buf[4] << 24) | (buf[5] << 16) | (buf[6] << 8) | buf[7];
                    int len = (le >= 0 && le <= 248) ? le : (be >= 0 && be <= 248) ? be : 0;
                    if (buf[3] == T_HELLO && n > 4) len = Math.Max(0, n - 8);
                    if (n >= 8 + len) { var r = new byte[n]; Array.Copy(buf, r, n); return r; }
                }
            }
            return null;
        }

        bool Handshake()
        {
            port.DiscardInBuffer();
            Write(T_HELLO, new byte[] { 1 });
            var id = ReadReply();
            if (id == null) return false;
            if (id != null && id.Length > 8)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 4; i < id.Length; i++) if (id[i] >= 32 && id[i] < 127) sb.Append((char)id[i]);
                if (sb.Length >= 3) Model = sb.ToString().Trim() + " (Smart Screen V28)";
            }
            Write(T_RES, new byte[] { 1 });
            var r = ReadReply();
            if (r != null && r.Length >= 12 && r[3] == T_RES)
            {
                int wv = (r[8] << 8) | r[9], hv = (r[10] << 8) | r[11];
                if (wv >= 64 && wv <= 1024 && hv >= 64 && hv <= 1024) { Width = wv; Height = hv; }
            }
            return true;
        }

        public void SetBrightness(int percent)
        {
            percent = Math.Max(1, Math.Min(100, percent));
            Write(T_BRIGHT, new byte[] { (byte)((percent * 255 + 50) / 100) });
        }

        public void SendFrame(byte[] h264) { Write(T_VIDEO, h264); }

        public void Dispose()
        {
            try { port.Close(); } catch { }
        }
    }
}
