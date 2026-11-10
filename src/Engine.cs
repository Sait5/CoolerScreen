using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;

namespace CoolerScreen
{
    // Фоновый поток: рисует кадры и шлёт их на экран. Спит между кадрами — CPU почти не тратит.
    public sealed class Engine
    {
        Settings cfg;
        CustomLayout layout;
        Bitmap drawing;                       // рисунок пользователя (копия)
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool stop, reload = true;
        Thread th;

        Lcd lcd;
        readonly H264Pcm enc = new H264Pcm(Render.S, Render.S);
        readonly Stopwatch sinceSend = new Stopwatch();
        Sensors sensors;
        MediaClip clip;
        string clipError;
        readonly Yuv work = new Yuv(Render.S, Render.S);
        Yuv rotBuf;
        int sentBrightness = -1;

        public readonly History Hist = new History();

        // Плавность: показываемые значения «доезжают» до настоящих за ~0,5 с (≈12 кадров/с),
        // а когда всё успокоилось — кадры не меняются и на экран не уходят.
        SensorData shown; bool shownInit;
        const int EaseFrameMs = 80;
        static float Step(ref float v, float target, ref bool moving)
        {
            float d = target - v;
            if (Math.Abs(d) <= 0.05f + Math.Abs(target) * 0.002f) { v = target; return v; }
            v += d * 0.35f; moving = true; return v;
        }
        bool Ease()
        {
            var t = Last;
            if (!shownInit) { shown = t; shownInit = true; return false; }
            bool m = false;
            Step(ref shown.CpuLoad, t.CpuLoad, ref m); Step(ref shown.RamLoad, t.RamLoad, ref m);
            Step(ref shown.RamUsedGb, t.RamUsedGb, ref m); shown.RamTotalGb = t.RamTotalGb;
            Step(ref shown.GpuTemp, t.GpuTemp, ref m); Step(ref shown.GpuHotspot, t.GpuHotspot, ref m);
            Step(ref shown.GpuLoad, t.GpuLoad, ref m); Step(ref shown.GpuPower, t.GpuPower, ref m);
            Step(ref shown.GpuClock, t.GpuClock, ref m); Step(ref shown.GpuFan, t.GpuFan, ref m);
            shown.VramMb = t.VramMb; shown.HasGpu = t.HasGpu;
            return m;
        }
        public SensorData Last;
        public volatile bool PreviewWanted;
        public event Action<Bitmap> PreviewFrame;     // готовый кадр (темы, конструктор)
        public event Action<Bitmap> PreviewOverlay;   // слой датчиков (для GIF и рисунка)
        public event Action StatusChanged;
        public string Status { get; private set; }
        public string DeviceName { get; private set; }

        public static void Log(string s)
        {
            try
            {
                string p = Path.Combine(Settings.Dir, "log.txt");
                Directory.CreateDirectory(Settings.Dir);
                if (File.Exists(p) && new FileInfo(p).Length > 200000) File.Delete(p);
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n");
            }
            catch { }
        }

        public Engine(Settings s, CustomLayout l, Bitmap drawingCopy)
        {
            cfg = s.Clone(); layout = l.Clone(); drawing = drawingCopy;
            Status = "Запуск…";
        }

        public void Start()
        {
            th = new Thread(Loop) { IsBackground = true, Name = "lcd", Priority = ThreadPriority.AboveNormal };
            th.Start();
        }

        public void Stop()
        {
            stop = true; wake.Set();
            // ждём, пока поток допишет текущий кадр: оборванный кадр вешает экран
            if (th != null) th.Join(8000);
            if (lcd != null) { try { lcd.Dispose(); } catch { } lcd = null; }
        }

        // «Остановить всё»: отпускаем экран полностью (порт закрыт), пока не нажмут «Запустить»
        volatile bool paused;
        public bool Paused { get { return paused; } }
        public void SetPaused(bool p) { paused = p; wake.Set(); }

        // Программы, которые тоже лезут к экрану и мешают
        public static readonly string[] Rivals = { "astroshel-lcd", "SmartMonitor28", "SmartMonitor28 A3", "Smart Screen V28", "Jungle Leopard Display" };
        public static string[] FindRivals()
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var n in Rivals) try { if (Process.GetProcessesByName(n).Length > 0) list.Add(n); } catch { }
            return list.ToArray();
        }
        public static int KillRivals()
        {
            int k = 0;
            foreach (var n in Rivals)
                try { foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); p.WaitForExit(3000); k++; } catch { } } } catch { }
            return k;
        }

        public void Apply(Settings s)
        {
            lock (gate)
            {
                bool mediaChanged = s.Mode != cfg.Mode || s.MediaPath != cfg.MediaPath || s.Fit != cfg.Fit;
                bool switched = mediaChanged || s.Theme != cfg.Theme || s.Rotation != cfg.Rotation;
                cfg = s.Clone();
                if (mediaChanged) reload = true;
                // Быстрые переключения туда-сюда вешали экран: ждём, пока пользователь «успокоится»,
                // и только потом шлём новое содержимое (каждый новый клик продлевает паузу).
                if (switched) settleUntil = DateTime.UtcNow.AddMilliseconds(SettleMs);
            }
            wake.Set();
        }

        const int SettleMs = 700;
        DateTime settleUntil = DateTime.MinValue;

        public void SetLayout(CustomLayout l) { lock (gate) layout = l.Clone(); wake.Set(); }
        public void SetDrawing(Bitmap copy) { Bitmap old; lock (gate) { old = drawing; drawing = copy; } if (old != null) old.Dispose(); wake.Set(); }

        void SetStatus(string s)
        {
            if (s == Status) return;
            Status = s;
            Log(s);
            var h = StatusChanged; if (h != null) h();
        }

        // Экран понимает только самостоятельные кадры (частичные P-кадры дают зелёный мусор),
        // поэтому шлём полный кадр, но только если картинка изменилась или пора «подтвердить» экран (раз в 2 с).
        byte[] lastPkt;
        Yuv lastYuv;
        MfEncoder mf;
        bool mfFailed;
        public string EncoderName = "";

        // Сжатый кадр (кодировщик Windows), при сбое — несжатый запасной вариант
        byte[] EncodeFrame(Yuv f)
        {
            if (mf == null && !mfFailed)
            {
                try { mf = new MfEncoder(Render.S, Render.S, 70); EncoderName = "сжатие H.264"; }
                catch (Exception e) { mfFailed = true; EncoderName = "без сжатия"; Log("кодировщик Windows недоступен: " + e.Message); }
            }
            if (mf != null)
            {
                try { var p = mf.Encode(f); if (p != null && p.Length > 0) return p; }
                catch (Exception e) { Log("ошибка сжатия: " + e.Message); try { mf.Dispose(); } catch { } mf = null; mfFailed = true; EncoderName = "без сжатия"; }
            }
            return enc.Idr(f);
        }

        static bool SameYuv(Yuv a, Yuv b)
        {
            if (a == null || b == null) return false;
            for (int i = 0; i < a.Y.Length; i++) if (a.Y[i] != b.Y[i]) return false;
            for (int i = 0; i < a.U.Length; i++) if (a.U[i] != b.U[i] || a.V[i] != b.V[i]) return false;
            return true;
        }

        void Send(Yuv f, int rotation)
        {
            if (rotation != 0) { rotBuf = f.Rotate(rotation, rotBuf); f = rotBuf; }
            bool same = lastPkt != null && SameYuv(lastYuv, f);
            if (same && sinceSend.ElapsedMilliseconds < 2000) return;
            if (!same)
            {
                lastPkt = EncodeFrame(f);
                if (lastYuv == null) lastYuv = new Yuv(f.W, f.H);
                Buffer.BlockCopy(f.Y, 0, lastYuv.Y, 0, f.Y.Length);
                Buffer.BlockCopy(f.U, 0, lastYuv.U, 0, f.U.Length);
                Buffer.BlockCopy(f.V, 0, lastYuv.V, 0, f.V.Length);
            }
            SendPacket(lastPkt);
        }

        // Ограничитель: экран зависает, если завалить его данными (большие кадры подряд).
        // Не больше ~300 КБ/с и не больше 25 кадров/с, что бы ни выбрал пользователь.
        const int MaxBytesPerSec = 300000, MinFrameMs = 40;   // спокойный темп: до 25 кадров/с
        readonly Stopwatch pace = Stopwatch.StartNew();
        long lastPktBytes;

        void SendPacket(byte[] pkt)
        {
            long need = Math.Max(MinFrameMs, lastPktBytes * 1000 / MaxBytesPerSec);
            long left = need - pace.ElapsedMilliseconds;
            if (left > 0) Thread.Sleep((int)left);
            pace.Restart(); lastPktBytes = pkt.Length;
            sinceSend.Restart();
            lcd.SendFrame(pkt);
            sentFrames++; sentBytes += pkt.Length;
            if ((DateTime.UtcNow - statAt).TotalSeconds >= 30)
            {
                Log("за 30 с: " + sentFrames + " кадров, средний кадр " + (sentFrames > 0 ? sentBytes / sentFrames / 1024 : 0) + " КБ, " + EncoderName);
                sentFrames = 0; sentBytes = 0; statAt = DateTime.UtcNow;
            }
        }
        int sentFrames; long sentBytes; DateTime statAt = DateTime.UtcNow;

        void SendBitmap(Bitmap bmp, int rotation) { Send(Yuv.FromBgra(Render.Bgra(bmp), Render.S, Render.S), rotation); }

        void Raise(Action<Bitmap> ev, Bitmap b) { if (!PreviewWanted) return; var h = ev; if (h != null) h((Bitmap)b.Clone()); }

        static bool IsNight() { int h = DateTime.Now.Hour; return h >= 23 || h < 8; }

        void Loop()
        {
            try { sensors = new Sensors(); } catch { }
            DateTime nextSensor = DateTime.MinValue;
            OverlayYuv overlayYuv = null;
            Bitmap overlayBmp = null;
            bool overlayFresh = false;
            int gifIdx = 0;
            DateTime gifDeadline = DateTime.UtcNow;

            while (!stop)
            {
                if (DateTime.UtcNow >= nextSensor)
                {
                    Last = sensors != null ? sensors.Read() : new SensorData();
                    Hist.Push(Last);
                    // читаем ровно на границе секунды, сразу перед кадром — без пропусков обновлений
                    var nowS = DateTime.UtcNow;
                    nextSensor = new DateTime(nowS.Ticks - nowS.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(1);
                    overlayFresh = false;
                }
                if (paused)
                {
                    if (lcd != null) { try { lcd.Dispose(); } catch { } lcd = null; }
                    SetStatus("Вывод остановлен — экран свободен");
                    wake.WaitOne(1000); continue;
                }
                if (lcd == null)
                {
                    string why = "Экран не найден — жду подключения…";
                    try { lcd = Lcd.Open(cfg.Brightness, SetStatus); }
                    catch (UnauthorizedAccessException)
                    {
                        lcd = null;
                        var r = FindRivals();
                        why = "Экран занят " + (r.Length > 0 ? "программой " + r[0] : "другой программой") + " — нажми «Остановить всё»";
                    }
                    catch (Exception e) { lcd = null; why = "Не удалось открыть экран: " + e.Message; }
                    if (lcd == null) { SetStatus(why); wake.WaitOne(2000); continue; }
                    if (!lcd.Answered) Log("экран не ответил на приветствие даже после оживления");
                    DeviceName = lcd.Model;
                    enc.Reset(); lastPkt = null; lastYuv = null; sentBrightness = -1; if (clip != null) clip.Packets = null;
                }

                var settleLeft = (settleUntil - DateTime.UtcNow).TotalMilliseconds;
                if (settleLeft > 0) { wake.WaitOne((int)settleLeft + 5); continue; }

                Settings s; CustomLayout lay;
                lock (gate) { s = cfg; lay = layout; }
                if (reload)
                {
                    reload = false; clip = null; clipError = null; gifIdx = 0;
                    if (s.Mode == Mode.Media && File.Exists(s.MediaPath))
                    {
                        SetStatus("Готовлю " + Path.GetFileName(s.MediaPath) + "…");
                        try { clip = MediaClip.Load(s.MediaPath, s.Fit); }
                        catch (Exception e) { clipError = e.Message; Log("ошибка файла: " + e.Message); }
                        GC.Collect();
                    }
                    gifDeadline = DateTime.UtcNow;
                }

                try
                {
                    int bright = s.NightDim && IsNight() ? Math.Min(s.Brightness, 15) : s.Brightness;
                    if (bright != sentBrightness) { lcd.SetBrightness(bright); sentBrightness = bright; }

                    bool needOverlay = s.Overlay && (s.Mode == Mode.Media || s.Mode == Mode.Drawing);
                    if (needOverlay && !overlayFresh)
                    {
                        if (overlayBmp != null) overlayBmp.Dispose();
                        overlayBmp = Render.Overlay(Last, s.Accent);
                        overlayYuv = new OverlayYuv(overlayBmp);
                        overlayFresh = true;
                        Raise(PreviewOverlay, overlayBmp);
                    }
                    string port = "Экран " + lcd.PortName + " · ";
                    int waitMs;

                    if (s.Mode == Mode.Media && clip != null)
                    {
                        var src = clip.Frames[gifIdx];
                        if (needOverlay && overlayYuv != null)
                        {
                            Buffer.BlockCopy(src.Y, 0, work.Y, 0, src.Y.Length);
                            Buffer.BlockCopy(src.U, 0, work.U, 0, src.U.Length);
                            Buffer.BlockCopy(src.V, 0, work.V, 0, src.V.Length);
                            overlayYuv.BlendInto(work);
                            Send(work, s.Rotation);
                        }
                        else if (clip.Animated)
                        {
                            // без слоя датчиков каждый кадр GIF сжимается один раз и дальше просто отправляется
                            if (clip.Packets == null || clip.PacketsRot != s.Rotation) { clip.Packets = new byte[clip.Frames.Count][]; clip.PacketsRot = s.Rotation; }
                            var pk = clip.Packets[gifIdx];
                            if (pk == null)
                            {
                                Yuv fr = s.Rotation != 0 ? src.Rotate(s.Rotation, rotBuf) : src;
                                if (s.Rotation != 0) rotBuf = fr;
                                pk = clip.Packets[gifIdx] = EncodeFrame(fr);
                            }
                            lastPkt = null; lastYuv = null;
                            SendPacket(pk);
                        }
                        else Send(src, s.Rotation);
                        SetStatus(port + (clip.Animated ? "GIF, " + clip.Frames.Count + " кадров" : "картинка"));
                        if (clip.Animated)
                        {
                            gifDeadline = gifDeadline.AddMilliseconds(clip.Delays[gifIdx]);
                            gifIdx = (gifIdx + 1) % clip.Frames.Count;
                            var n2 = DateTime.UtcNow;
                            if (gifDeadline < n2.AddMilliseconds(-500)) gifDeadline = n2;
                            waitMs = (int)(gifDeadline - n2).TotalMilliseconds;
                        }
                        else waitMs = 1000;
                    }
                    else if (s.Mode == Mode.Drawing)
                    {
                        using (var bmp = new Bitmap(Render.S, Render.S))
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                lock (gate) { if (drawing != null) g.DrawImageUnscaled(drawing, 0, 0); else g.Clear(Color.Black); }
                                if (needOverlay && overlayBmp != null) g.DrawImageUnscaled(overlayBmp, 0, 0);
                            }
                            SendBitmap(bmp, s.Rotation);
                        }
                        SetStatus(port + "рисунок");
                        waitMs = 1000;
                    }
                    else if (s.Mode == Mode.Custom)
                    {
                        bool moving = Ease();
                        using (var bmp = lay.Render(shown, s.Accent, s.CustomBg))
                        {
                            SendBitmap(bmp, s.Rotation);
                            Raise(PreviewFrame, bmp);
                        }
                        SetStatus(port + "свой дашборд");
                        waitMs = moving ? EaseFrameMs : 1000 - DateTime.Now.Millisecond + 5;
                    }
                    else
                    {
                        bool moving = Ease();
                        using (var bmp = Themes.Draw(s.Theme, shown, Hist, s.Accent))
                        {
                            SendBitmap(bmp, s.Rotation);
                            Raise(PreviewFrame, bmp);
                        }
                        string extra = s.Mode == Mode.Media ? (clipError != null ? " (файл не открылся)" : " (файл не выбран)") : "";
                        SetStatus(port + "тема «" + Themes.Names[s.Theme] + "»" + extra);
                        int iv = Themes.Interval(s.Theme);
                        waitMs = iv < 1000 ? iv : moving ? EaseFrameMs : 1000 - DateTime.Now.Millisecond + 5;
                    }
                    if (waitMs > 0) wake.WaitOne(waitMs);
                }
                catch (Exception e)
                {
                    Log("ошибка: " + e.Message);
                    SetStatus("Связь с экраном потеряна — переподключаюсь…");
                    try { lcd.Dispose(); } catch { }
                    lcd = null;
                    wake.WaitOne(1500);
                }
            }
        }
    }
}
