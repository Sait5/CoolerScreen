using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CoolerScreen
{
    // Окно целиком рисуется вручную: тёмная тема, скругления, свои переключатели.
    public sealed class MainForm : Form
    {
        static readonly Color CBg = Color.FromArgb(13, 15, 21), CCard = Color.FromArgb(21, 24, 34), CCard2 = Color.FromArgb(30, 34, 48),
            CLine = Color.FromArgb(44, 49, 68), CText = Color.FromArgb(232, 236, 245), CDim = Color.FromArgb(128, 138, 160), CFaint = Color.FromArgb(78, 86, 108);
        static readonly Color[] Swatches = {
            Color.FromArgb(0, 200, 255), Color.FromArgb(130, 100, 255), Color.FromArgb(255, 70, 150), Color.FromArgb(255, 140, 40),
            Color.FromArgb(60, 220, 130), Color.FromArgb(255, 220, 70), Color.FromArgb(240, 240, 250) };
        static readonly Color[] Paint = {
            Color.White, Color.FromArgb(20, 20, 24), Color.FromArgb(255, 60, 60), Color.FromArgb(255, 150, 40), Color.FromArgb(255, 225, 60),
            Color.FromArgb(60, 220, 110), Color.FromArgb(0, 200, 255), Color.FromArgb(60, 110, 255), Color.FromArgb(150, 90, 255), Color.FromArgb(255, 80, 170) };
        static readonly string[] Tabs = { "Темы", "GIF", "Рисунок", "Свой", "Настройки" };
        const int W = 820, H = 600, TitleH = 52, RX = 404;

        readonly Settings cfg;
        readonly Engine engine;
        readonly NotifyIcon tray;
        bool quitting, autostart;
        int tab;

        // превью
        Bitmap prevFrame, prevOverlay;
        Image media;
        readonly Rectangle rPreview = new Rectangle(40, TitleH + 34, 320, 320);
        Bitmap[] thumbs;
        DateTime thumbsAt;

        // рисовалка
        Bitmap canvas;
        readonly List<Bitmap> undo = new List<Bitmap>();
        Color brush = Color.White;
        int brushSize = 1;      // 0..2
        bool eraser, drawing;
        Point lastPt;
        DateTime lastPush;

        // свой дашборд
        CustomLayout layout;
        Widget sel;
        bool dragW, resizeW;
        float resizeStartScale, resizeStartDist;

        // углы выделенного блока: 0 — левый верх, 1 — правый верх, 2 — левый низ, 3 — правый низ
        static PointF[] Corners(Widget w)
        {
            var b = w.Bounds; b.Inflate(4, 4);
            return new[] { new PointF(b.Left, b.Top), new PointF(b.Right, b.Top), new PointF(b.Left, b.Bottom), new PointF(b.Right, b.Bottom) };
        }

        int HandleAt(PointF p)
        {
            if (sel == null || !layout.Items.Contains(sel)) return -1;
            var cs = Corners(sel);
            for (int i = 0; i < 4; i++) if (Math.Abs(p.X - cs[i].X) <= 8 && Math.Abs(p.Y - cs[i].Y) <= 8) return i;
            return -1;
        }

        void ResizeSel(float delta)
        {
            if (sel == null) return;
            sel.Scale = Math.Max(0.3f, Math.Min(4f, (float)Math.Round((sel.Scale + delta) * 10) / 10f));
            LayoutChanged(true);
        }
        PointF dragOff;
        TextBox txtEdit;

        // интерактивные зоны
        sealed class Hot { public Rectangle R; public Action Click; }
        readonly List<Hot> hots = new List<Hot>();
        Hot hover;
        Rectangle rSlider;
        bool dragSlider;
        string status = "Запуск…";
        readonly Font fTitle, fBody, fBodyB, fSmall, fCap;

        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        public MainForm(Settings s, bool startHidden)
        {
            cfg = s;
            Text = "Экран водянки";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            BackColor = CBg;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            AllowDrop = true;
            Icon = AppIcon();

            fTitle = new Font("Segoe UI Semibold", 11.5f);
            fBody = new Font("Segoe UI", 10f);
            fBodyB = new Font("Segoe UI Semibold", 10f);
            fSmall = new Font("Segoe UI", 8.5f);
            fCap = new Font("Segoe UI Semibold", 7.5f);
            autostart = AutostartOn();
            tab = (int)cfg.Mode == 0 ? 0 : cfg.Mode == Mode.Media ? 1 : cfg.Mode == Mode.Drawing ? 2 : 3;

            DragEnter += (o, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (o, e) => { var f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f != null && f.Length > 0) SetFile(f[0]); };

            LoadMedia();
            layout = CustomLayout.Load();
            canvas = LoadCanvas();

            txtEdit = new TextBox { Visible = false, BorderStyle = BorderStyle.FixedSingle, BackColor = CCard2, ForeColor = CText, Font = new Font("Segoe UI", 10.5f), MaxLength = 40 };
            txtEdit.TextChanged += (o, e) => { if (sel != null && sel.Kind == WKind.Text) { sel.Text = txtEdit.Text; LayoutChanged(true); } };
            Controls.Add(txtEdit);

            engine = new Engine(cfg, layout, (Bitmap)canvas.Clone());
            engine.PreviewOverlay += b => Ui(() => { Swap(ref prevOverlay, b); Invalidate(rPreview); }, b);
            engine.StatusChanged += () => Ui(() =>
            {
                status = engine.Status;
                tray.Text = Cut("Экран водянки — " + status, 63);
                Invalidate();
            }, null);

            tray = new NotifyIcon { Icon = new Icon(Icon, SystemInformation.SmallIconSize), Text = "Экран водянки", Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, (o, e) => ShowWin());
            var miStop = new ToolStripMenuItem("Остановить всё", null, (o, e) => ToggleStopAll());
            menu.Items.Add(miStop);
            menu.Opening += (o, e) => miStop.Text = engine.Paused ? "Запустить вывод на экран" : "Остановить всё";
            menu.Items.Add("Выход", null, (o, e) => { quitting = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ShowWin(); };

            if (startHidden) { ShowInTaskbar = false; Opacity = 0; }
            Load += (o, e) =>
            {
                int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4);
                v = 1; DwmSetWindowAttribute(Handle, 20, ref v, 4);
                if (startHidden) BeginInvoke((Action)(() => { HideWin(); Opacity = 1; }));
                else { engine.PreviewWanted = true; BeginInvoke((Action)(() => { ShowWindow(Handle, 1); Activate(); })); }
            };
            engine.Start();
            if (Program.ShowSignal != null)
                new System.Threading.Thread(() => { while (Program.ShowSignal.WaitOne()) Ui(ShowWin, null); }) { IsBackground = true }.Start();
            if (Program.QuitSignal != null)
                new System.Threading.Thread(() => { if (Program.QuitSignal.WaitOne()) Ui(() => { quitting = true; Close(); }, null); }) { IsBackground = true }.Start();

            var tick = new Timer { Interval = 1000 };
            tick.Tick += (o, e) =>
            {
                if (!Visible) return;
                var rv = Engine.FindRivals();
                if (string.Join(",", rv) != string.Join(",", rivals)) { rivals = rv; Invalidate(); }
                Invalidate(new Rectangle(0, H - 40, W, 40));
                if (cfg.Mode == Mode.Custom || cfg.Mode == Mode.Dashboard) Invalidate(rPreview);
                if (tab == 0 && (DateTime.Now - thumbsAt).TotalSeconds >= 2) { MakeThumbs(); Invalidate(); }
            };
            tick.Start();
            var fast = new Timer { Interval = 100 };   // анимация темы «Жидкость» в превью
            fast.Tick += (o, e) => { if (Visible && cfg.Mode == Mode.Dashboard && Themes.Interval(cfg.Theme) < 1000) Invalidate(rPreview); };
            fast.Start();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; cp.Style |= 0x20000; return cp; }
        }

        void Ui(Action a, IDisposable orDispose)
        {
            if (IsHandleCreated && !IsDisposed) try { BeginInvoke(a); return; } catch { }
            if (orDispose != null) orDispose.Dispose();
        }
        static void Swap(ref Bitmap slot, Bitmap b) { var old = slot; slot = b; if (old != null) old.Dispose(); }
        static string Cut(string s, int n) { return s.Length > n ? s.Substring(0, n) : s; }

        // ================= рисование окна =================

        static GraphicsPath RR(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        static void Fill(Graphics g, RectangleF r, float rad, Color c) { using (var p = RR(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }
        static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w = 1) { using (var p = RR(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p); }
        void Txt(Graphics g, string s, Font f, Color c, float x, float y) { TextRenderer.DrawText(g, s, f, new Point((int)x, (int)y), c, TextFormatFlags.NoPadding); }
        void TxtR(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags fl) { TextRenderer.DrawText(g, s, f, r, c, fl | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis); }
        Hot Add(Rectangle r, Action a) { var h = new Hot { R = r, Click = a }; hots.Add(h); return h; }
        bool Hov(Rectangle r) { return hover != null && hover.R == r; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            hots.Clear();
            Color A = cfg.Accent;

            using (var path = new GraphicsPath())
            {
                path.AddEllipse(-180, -260, 760, 620);
                using (var pg = new PathGradientBrush(path) { CenterColor = Color.FromArgb(38, A), SurroundColors = new[] { Color.FromArgb(0, A) } })
                    g.FillPath(pg, path);
            }

            // заголовок
            DrawLogo(g, 22, 15, A);
            Txt(g, "Экран водянки", fTitle, CText, 54, 15);
            Txt(g, "320 × 320 · лёгкий режим", fSmall, CDim, 56, 34);
            var rMin = new Rectangle(W - 92, 10, 36, 32); var rClose = new Rectangle(W - 50, 10, 36, 32);
            if (Hov(rMin)) Fill(g, rMin, 8, CCard2);
            if (Hov(rClose)) Fill(g, rClose, 8, Color.FromArgb(200, 50, 60));
            using (var p = new Pen(CText, 1.4f)) { g.DrawLine(p, rMin.X + 13, 26, rMin.X + 23, 26); g.DrawLine(p, rClose.X + 13, 21, rClose.X + 23, 31); g.DrawLine(p, rClose.X + 23, 21, rClose.X + 13, 31); }
            Add(rMin, HideWin); Add(rClose, HideWin);

            // превью
            var card = new Rectangle(rPreview.X - 18, rPreview.Y - 18, rPreview.Width + 36, rPreview.Height + 36);
            Fill(g, card, 26, CCard);
            Stroke(g, card, 26, Color.FromArgb(50, A), 1.2f);
            using (var clip = RR(rPreview, 14))
            {
                var st = g.Save();
                g.SetClip(clip);
                DrawPreview(g);
                g.Restore(st);
            }
            Stroke(g, rPreview, 14, Color.FromArgb(60, 255, 255, 255));

            bool ok = status.StartsWith("Экран COM");
            var pill = new Rectangle(card.X, card.Bottom + 14, card.Width, 34);
            Fill(g, pill, 17, CCard);
            using (var b = new SolidBrush(ok ? Color.FromArgb(60, 220, 130) : Color.FromArgb(255, 170, 40))) g.FillEllipse(b, pill.X + 16, pill.Y + 13, 8, 8);
            TxtR(g, status, fSmall, ok ? CText : CDim, new Rectangle(pill.X + 32, pill.Y, pill.Width - 44, pill.Height), TextFormatFlags.VerticalCenter);
            string hint = tab == 2 ? "Рисуй мышкой прямо на превью" : tab == 3 ? "Тащи блок · уголки или колёсико — размер · Del — удалить" : null;
            if (hint != null) TxtR(g, hint, fSmall, CDim, new Rectangle(card.X, pill.Bottom + 10, card.Width, 18), TextFormatFlags.HorizontalCenter);
            // «Остановить всё» — закрыть мешающие программы и отпустить экран; повторное нажатие запускает вывод
            var rStop = new Rectangle(card.X, pill.Bottom + 36, card.Width, 40);
            bool paused = engine.Paused;
            Fill(g, rStop, 12, paused ? Color.FromArgb(48, A) : Hov(rStop) ? Color.FromArgb(70, 200, 50, 60) : CCard);
            Stroke(g, rStop, 12, paused ? Color.FromArgb(140, A) : Color.FromArgb(110, 220, 70, 80), 1.2f);
            TxtR(g, paused ? "▶  Запустить вывод на экран" : "■  Остановить всё", fBodyB, CText, rStop, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Add(rStop, ToggleStopAll);
            if (rivals.Length > 0)
                TxtR(g, "Мешает: " + string.Join(", ", rivals), fSmall, Color.FromArgb(255, 170, 40), new Rectangle(card.X, rStop.Bottom + 6, card.Width, 18), TextFormatFlags.HorizontalCenter);

            // вкладки
            int x = RX, cw = W - RX - 28, y = TitleH + 14;
            Segmented(g, new Rectangle(x, y, cw, 40), Tabs, tab, SetTab, A);
            y += 56;
            bool showText = false;
            switch (tab)
            {
                case 0: PaintThemes(g, x, y, cw, A); break;
                case 1: PaintGif(g, x, y, cw, A); break;
                case 2: PaintDraw(g, x, y, cw, A); break;
                case 3: showText = PaintCustom(g, x, y, cw, A); break;
                default: PaintSettings(g, x, y, cw, A); break;
            }
            if (!showText && txtEdit.Visible) txtEdit.Visible = false;

            long mb = Environment.WorkingSet / (1024 * 1024);
            Txt(g, "Программа занимает " + mb + " МБ памяти · закрой окно — останется в трее", fSmall, CFaint, 24, H - 28);
        }

        void Cap(Graphics g, string s, int x, int y) { Txt(g, s, fCap, CDim, x, y); }

        void Segmented(Graphics g, Rectangle r, string[] items, int sel, Action<int> pick, Color A)
        {
            Fill(g, r, r.Height / 2f, CCard);
            float iw = (r.Width - 8) / (float)items.Length;
            for (int i = 0; i < items.Length; i++)
            {
                var ir = new Rectangle((int)(r.X + 4 + iw * i), r.Y + 4, (int)iw, r.Height - 8);
                if (i == sel) { Fill(g, ir, ir.Height / 2f, Color.FromArgb(48, A)); Stroke(g, ir, ir.Height / 2f, Color.FromArgb(120, A)); }
                else if (Hov(ir)) Fill(g, ir, ir.Height / 2f, CCard2);
                TxtR(g, items[i], i == sel ? fBodyB : fBody, i == sel ? CText : CDim, ir, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                int ii = i; Add(ir, () => pick(ii));
            }
        }

        void Btn(Graphics g, Rectangle r, string text, Action a, Color A, bool on = false)
        {
            Fill(g, r, 10, on ? Color.FromArgb(48, A) : Hov(r) ? CCard2 : CCard);
            Stroke(g, r, 10, on ? Color.FromArgb(120, A) : CLine);
            TxtR(g, text, fBody, on ? CText : Hov(r) ? CText : Color.FromArgb(200, 206, 220), r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Add(r, a);
        }

        void Toggle(Graphics g, int x, int y, int w, string title, string sub, bool on, Action flip, Color A)
        {
            Txt(g, title, fBody, CText, x, y + 1);
            if (sub != null) Txt(g, sub, fSmall, CDim, x, y + 21);
            var sw = new Rectangle(x + w - 46, y + 6, 46, 26);
            Fill(g, sw, 13, on ? A : CCard2);
            if (!on) Stroke(g, sw, 13, CLine);
            using (var b = new SolidBrush(on ? Color.FromArgb(14, 16, 22) : CDim))
                g.FillEllipse(b, on ? sw.Right - 22 : sw.X + 4, sw.Y + 4, 18, 18);
            Add(new Rectangle(x, y, w, 40), flip);
        }

        int SwatchRow(Graphics g, int x, int y, Color[] cols, Color cur, Action<Color> pick, bool custom, int size = 28, int step = 42)
        {
            foreach (var c in cols)
            {
                var r = new Rectangle(x, y, size, size);
                if (c.ToArgb() == cur.ToArgb()) using (var pen = new Pen(c.GetBrightness() < 0.2f ? CDim : c, 2)) g.DrawEllipse(pen, r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
                if (c.GetBrightness() < 0.15f) using (var pen = new Pen(CLine)) g.DrawEllipse(pen, r);
                var cc = c; Add(r, () => pick(cc));
                x += step;
            }
            if (custom)
            {
                var rc = new Rectangle(x, y, size, size);
                bool isCustom = Array.FindIndex(cols, c => c.ToArgb() == cur.ToArgb()) < 0;
                using (var pen = new Pen(isCustom ? cur : CFaint, isCustom ? 2 : 1.3f)) g.DrawEllipse(pen, rc);
                int m = size / 2;
                using (var pen = new Pen(CDim, 1.6f)) { g.DrawLine(pen, rc.X + m - 5, rc.Y + m, rc.X + m + 5, rc.Y + m); g.DrawLine(pen, rc.X + m, rc.Y + m - 5, rc.X + m, rc.Y + m + 5); }
                Add(rc, () => { using (var d = new ColorDialog { Color = cur, FullOpen = true }) if (d.ShowDialog(this) == DialogResult.OK) pick(d.Color); });
            }
            return y + size;
        }

        // ---------- вкладка «Темы» ----------
        void MakeThumbs()
        {
            if (thumbs == null) thumbs = new Bitmap[Themes.Names.Length];
            for (int i = 0; i < thumbs.Length; i++)
            {
                using (var full = Themes.Draw(i, engine.Last, engine.Hist, cfg.Accent))
                {
                    var t = new Bitmap(104, 104);
                    using (var g = Graphics.FromImage(t)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, 104, 104); }
                    Swap(ref thumbs[i], t);
                }
            }
            thumbsAt = DateTime.Now;
        }

        void PaintThemes(Graphics g, int x, int y, int cw, Color A)
        {
            if (thumbs == null) MakeThumbs();
            int cols = 3, gap = 14, tw = (cw - gap * (cols - 1)) / cols;
            for (int i = 0; i < Themes.Names.Length; i++)
            {
                int cx = x + (i % cols) * (tw + gap), cy = y + (i / cols) * 140;
                var cell = new Rectangle(cx, cy, tw, 132);
                bool on = cfg.Mode == Mode.Dashboard && cfg.Theme == i;
                Fill(g, cell, 14, on ? Color.FromArgb(40, A) : Hov(cell) ? CCard2 : CCard);
                if (on) Stroke(g, cell, 14, A, 1.5f);
                var tr = new Rectangle(cx + (tw - 104) / 2, cy + 8, 104, 104);
                using (var clip = RR(tr, 10))
                {
                    var st = g.Save(); g.SetClip(clip);
                    if (thumbs[i] != null) g.DrawImageUnscaled(thumbs[i], tr.X, tr.Y);
                    g.Restore(st);
                }
                TxtR(g, Themes.Names[i], on ? fBodyB : fSmall, on ? CText : CDim, new Rectangle(cx, cy + 112, tw, 18), TextFormatFlags.HorizontalCenter);
                int ii = i; Add(cell, () => { cfg.Theme = ii; cfg.Mode = Mode.Dashboard; Changed(); });
            }
        }

        // ---------- вкладка «GIF» ----------
        void PaintGif(Graphics g, int x, int y, int cw, Color A)
        {
            Cap(g, "ФАЙЛ", x, y); y += 20;
            var drop = new Rectangle(x, y, cw, 84);
            bool dh = Hov(drop);
            Fill(g, drop, 16, dh ? CCard2 : CCard);
            using (var p = RR(drop, 16)) using (var pen = new Pen(dh ? A : CLine, 1.3f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, p);
            DrawFileIcon(g, drop.X + 18, drop.Y + 24, A);
            bool hasFile = !string.IsNullOrEmpty(cfg.MediaPath) && File.Exists(cfg.MediaPath);
            TxtR(g, hasFile ? Path.GetFileName(cfg.MediaPath) : "Перетащи сюда GIF или картинку", fBodyB, CText, new Rectangle(drop.X + 66, drop.Y + 21, drop.Width - 80, 22), 0);
            TxtR(g, hasFile ? MediaInfo() + " · нажми, чтобы сменить" : "или нажми, чтобы выбрать файл", fSmall, CDim, new Rectangle(drop.X + 66, drop.Y + 45, drop.Width - 80, 18), 0);
            Add(drop, PickFile);
            y += 100;
            Cap(g, "МАСШТАБ", x, y); y += 20;
            Segmented(g, new Rectangle(x, y, cw, 38), new[] { "Заполнить", "Вписать", "Растянуть" }, (int)cfg.Fit,
                i => { cfg.Fit = (Render.Fit)i; if (media != null) cfg.Mode = Mode.Media; Changed(); }, A);
            y += 56;
            Toggle(g, x, y, cw, "Датчики поверх GIF", "температура, загрузка и время внизу", cfg.Overlay, () => { cfg.Overlay = !cfg.Overlay; Changed(); }, A);
            y += 56;
            if (hasFile && cfg.Mode != Mode.Media)
                Btn(g, new Rectangle(x, y, cw, 38), "Показать эту GIF на экране", () => { cfg.Mode = Mode.Media; Changed(); }, A, true);
        }

        // ---------- вкладка «Рисунок» ----------
        void PaintDraw(Graphics g, int x, int y, int cw, Color A)
        {
            Cap(g, "ИНСТРУМЕНТ", x, y); y += 20;
            Segmented(g, new Rectangle(x, y, cw, 38), new[] { "Кисть", "Ластик" }, eraser ? 1 : 0, i => { eraser = i == 1; Invalidate(); }, A);
            y += 50;
            Segmented(g, new Rectangle(x, y, cw, 34), new[] { "Тонкая", "Средняя", "Толстая" }, brushSize, i => { brushSize = i; Invalidate(); }, A);
            y += 50;
            Cap(g, "ЦВЕТ КИСТИ", x, y); y += 22;
            y = SwatchRow(g, x + 2, y, Paint, brush, c => { brush = c; eraser = false; Invalidate(); }, true, 26, 34) + 22;
            int bw = (cw - 20) / 3;
            Btn(g, new Rectangle(x, y, bw, 36), "Залить фон", FillCanvas, A);
            Btn(g, new Rectangle(x + bw + 10, y, bw, 36), "Отменить", Undo, A);
            Btn(g, new Rectangle(x + 2 * (bw + 10), y, bw, 36), "Очистить", () => { PushUndo(); using (var cg = Graphics.FromImage(canvas)) cg.Clear(Color.Black); CanvasChanged(true); }, A);
            y += 46;
            Btn(g, new Rectangle(x, y, cw, 36), "Подложить картинку под рисунок…", LoadImageToCanvas, A);
            y += 52;
            Toggle(g, x, y, cw, "Датчики поверх рисунка", null, cfg.Overlay, () => { cfg.Overlay = !cfg.Overlay; Changed(); }, A);
        }

        // ---------- вкладка «Свой» ----------
        bool PaintCustom(Graphics g, int x, int y, int cw, Color A)
        {
            Cap(g, "ДОБАВИТЬ БЛОК", x, y); y += 20;
            int cols = 4, gap = 6, bw = (cw - gap * (cols - 1)) / cols;
            for (int i = 0; i < Widget.KindNames.Length; i++)
            {
                var r = new Rectangle(x + (i % cols) * (bw + gap), y + (i / cols) * 34, bw, 28);
                var kind = (WKind)i;
                Fill(g, r, 8, Hov(r) ? CCard2 : CCard);
                Stroke(g, r, 8, CLine);
                TxtR(g, "+ " + Widget.KindNames[i], fSmall, CText, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Add(r, () => AddWidget(kind));
            }
            y += 3 * 34 + 14;

            bool showText = false;
            if (sel != null && layout.Items.Contains(sel))
            {
                Cap(g, "ВЫБРАН: " + Widget.KindNames[(int)sel.Kind].ToUpper(), x, y);
                var rDel = new Rectangle(x + cw - 84, y - 6, 84, 24);
                Btn(g, rDel, "Удалить", DeleteSel, A);
                var rPlus = new Rectangle(rDel.X - 10 - 26, y - 6, 26, 24);
                var rMinus = new Rectangle(rPlus.X - 50 - 26, y - 6, 26, 24);
                Btn(g, rMinus, "−", () => ResizeSel(-0.1f), A);
                TxtR(g, (int)Math.Round(sel.Scale * 100) + "%", fBodyB, CText, new Rectangle(rMinus.Right, y - 6, 50, 24), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Btn(g, rPlus, "+", () => ResizeSel(0.1f), A);
                y += 24;
                if (sel.IsSensor)
                {
                    Segmented(g, new Rectangle(x, y, cw, 34), new[] { "Число", "Кольцо", "Полоса" }, (int)sel.Style, i => { sel.Style = (WStyle)i; LayoutChanged(true); }, A);
                    y += 44;
                }
                else if (sel.Kind == WKind.Text)
                {
                    txtEdit.SetBounds(x, y, cw, 28);
                    if (!txtEdit.Visible) { txtEdit.Text = sel.Text; txtEdit.Visible = true; }
                    showText = true;
                    y += 40;
                }
                var cols2 = new Color[Swatches.Length + 1];
                Array.Copy(Swatches, cols2, Swatches.Length);
                cols2[Swatches.Length] = Color.FromArgb(255, 70, 70);
                y = SwatchRow(g, x + 2, y, cols2, sel.Color ?? Color.Empty, c => { sel.Color = c; LayoutChanged(true); }, true, 24, 34);
                var rAuto = new Rectangle(x + cw - 60, y - 24, 60, 24);
                Btn(g, rAuto, "Авто", () => { sel.Color = null; LayoutChanged(true); }, A, !sel.Color.HasValue);
                y += 18;
            }
            else
            {
                TxtR(g, "Нажми на блок на превью, чтобы изменить его вид и цвет", fSmall, CDim, new Rectangle(x, y, cw, 18), 0);
                y += 30;
            }

            Cap(g, "ФОН", x, y); y += 20;
            int bgSel = string.IsNullOrEmpty(cfg.CustomBg) ? 0 : cfg.CustomBg.StartsWith("#") ? 1 : 2;
            Segmented(g, new Rectangle(x, y, cw - 96, 34), new[] { "Градиент", "Цвет", "Картинка" }, bgSel, PickBg, A);
            Btn(g, new Rectangle(x + cw - 88, y, 88, 34), "Сброс", () => { if (MessageBox.Show(this, "Вернуть стандартный набор блоков?", "Свой дашборд", MessageBoxButtons.YesNo) == DialogResult.Yes) { layout = CustomLayout.Default(); sel = null; LayoutChanged(true); } }, A);
            return showText;
        }

        // ---------- вкладка «Настройки» ----------
        void PaintSettings(Graphics g, int x, int y, int cw, Color A)
        {
            Cap(g, "ЦВЕТ АКЦЕНТА", x, y); y += 22;
            y = SwatchRow(g, x + 2, y, Swatches, cfg.Accent, SetAccent, true) + 22;

            Cap(g, "ЯРКОСТЬ ЭКРАНА", x, y);
            TxtR(g, cfg.Brightness + "%", fBodyB, CText, new Rectangle(x, y - 3, cw, 20), TextFormatFlags.Right);
            y += 26;
            rSlider = new Rectangle(x, y, cw, 22);
            float frac = (cfg.Brightness - 1) / 99f;
            Fill(g, new RectangleF(x, y + 8, cw, 6), 3, CCard2);
            using (var lg = new LinearGradientBrush(new RectangleF(x, y, cw, 6), Color.FromArgb(120, A), A, 0f))
            using (var p = RR(new RectangleF(x, y + 8, Math.Max(6, cw * frac), 6), 3)) g.FillPath(lg, p);
            float kx = x + cw * frac;
            using (var b = new SolidBrush(Color.FromArgb(60, A))) g.FillEllipse(b, kx - 13, y - 2, 26, 26);
            using (var b = new SolidBrush(CText)) g.FillEllipse(b, kx - 8, y + 3, 16, 16);
            hots.Add(new Hot { R = rSlider });
            y += 40;

            Cap(g, "ПОВОРОТ ИЗОБРАЖЕНИЯ (ЕСЛИ ПОМПА СТОИТ БОКОМ)", x, y); y += 20;
            Segmented(g, new Rectangle(x, y, cw, 34), new[] { "0°", "90°", "180°", "270°" }, cfg.Rotation, i => { cfg.Rotation = i; Changed(); }, A);
            y += 50;
            Toggle(g, x, y, cw, "Ночью приглушать экран", "с 23:00 до 08:00 яркость 15%", cfg.NightDim, () => { cfg.NightDim = !cfg.NightDim; Changed(); }, A);
            y += 50;
            Toggle(g, x, y, cw, "Запускать вместе с Windows", "сразу в трей, окно не открывается", autostart, () => { autostart = !autostart; SetAutostart(autostart); Invalidate(); }, A);
            y += 54;
            Cap(g, "УСТРОЙСТВО", x, y); y += 18;
            TxtR(g, engine.DeviceName ?? "не подключено", fBody, CText, new Rectangle(x, y, cw, 20), 0);
            TxtR(g, "Поддерживаются водянки с экраном Smart Screen V28 (USB 1F3A:0007)", fSmall, CDim, new Rectangle(x, y + 20, cw, 18), 0);
        }

        static void DrawLogo(Graphics g, int x, int y, Color A)
        {
            using (var b = new SolidBrush(CCard2)) g.FillEllipse(b, x, y, 24, 24);
            using (var p = new Pen(A, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, x + 4, y + 4, 16, 16, 135, 220);
            using (var b = new SolidBrush(A)) g.FillEllipse(b, x + 9.5f, y + 9.5f, 5, 5);
        }

        static void DrawFileIcon(Graphics g, int x, int y, Color A)
        {
            var r = new Rectangle(x, y, 34, 36);
            Fill(g, r, 9, Color.FromArgb(40, A));
            using (var p = new Pen(A, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(p, x + 17, y + 25, x + 17, y + 11);
                g.DrawLine(p, x + 11, y + 17, x + 17, y + 11);
                g.DrawLine(p, x + 23, y + 17, x + 17, y + 11);
                g.DrawLine(p, x + 10, y + 29, x + 24, y + 29);
            }
        }

        string MediaInfo()
        {
            if (media == null) return "не открылся";
            int n = 1; try { n = media.GetFrameCount(FrameDimension.Time); } catch { }
            return (n > 1 ? "GIF, " + n + " кадров" : "картинка") + ", " + media.Width + "×" + media.Height;
        }

        void DrawPreview(Graphics g)
        {
            var st = g.Save();
            g.TranslateTransform(rPreview.X, rPreview.Y);
            g.Clear(Color.Black);
            if (cfg.Mode == Mode.Media && media != null)
            {
                ImageAnimator.UpdateFrames(media);
                Render.DrawFitted(g, media, cfg.Fit);
                if (cfg.Overlay && prevOverlay != null) g.DrawImageUnscaled(prevOverlay, 0, 0);
            }
            else if (cfg.Mode == Mode.Drawing)
            {
                g.DrawImageUnscaled(canvas, 0, 0);
                if (cfg.Overlay && prevOverlay != null) g.DrawImageUnscaled(prevOverlay, 0, 0);
                Point cp = PointToClient(Cursor.Position);
                if (tab == 2 && rPreview.Contains(cp))
                {
                    float r = BrushPx() / 2f;
                    using (var p = new Pen(Color.FromArgb(160, 255, 255, 255), 1)) g.DrawEllipse(p, cp.X - rPreview.X - r, cp.Y - rPreview.Y - r, r * 2, r * 2);
                }
            }
            else if (cfg.Mode == Mode.Custom)
            {
                using (var bmp = layout.Render(engine.Last, cfg.Accent, cfg.CustomBg)) g.DrawImageUnscaled(bmp, 0, 0);
                if (tab == 3 && sel != null && layout.Items.Contains(sel))
                {
                    var b = sel.Bounds; b.Inflate(4, 4);
                    using (var p = new Pen(cfg.Accent, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, b.X, b.Y, b.Width, b.Height);
                    using (var fillB = new SolidBrush(Color.White))
                    using (var edge = new Pen(cfg.Accent, 1.5f))
                        foreach (var c in Corners(sel))
                        {
                            g.FillRectangle(fillB, c.X - 5, c.Y - 5, 10, 10);
                            g.DrawRectangle(edge, c.X - 5, c.Y - 5, 10, 10);
                        }
                }
            }
            else
                using (var bmp = Themes.Draw(cfg.Theme, engine.Last, engine.Hist, cfg.Accent)) g.DrawImageUnscaled(bmp, 0, 0);
            g.Restore(st);
        }

        // ================= мышь и клавиатура =================

        Hot HitTest(Point p) { for (int i = hots.Count - 1; i >= 0; i--) if (hots[i].R.Contains(p)) return hots[i]; return null; }
        PointF Local(Point p) { return new PointF(p.X - rPreview.X, p.Y - rPreview.Y); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragSlider) { SetSlider(e.X); return; }
            if (drawing) { StrokeTo(e.Location); return; }
            if (resizeW && sel != null)
            {
                var lp = Local(e.Location);
                float d = (float)Math.Sqrt((lp.X - sel.X) * (lp.X - sel.X) + (lp.Y - sel.Y) * (lp.Y - sel.Y));
                sel.Scale = Math.Max(0.3f, Math.Min(4f, resizeStartScale * d / Math.Max(4f, resizeStartDist)));
                LayoutChanged(false);
                Invalidate(new Rectangle(RX, TitleH, W - RX, H - TitleH));
                return;
            }
            if (dragW && sel != null)
            {
                var p = Local(e.Location);
                sel.X = Math.Max(0, Math.Min(Render.S, p.X - dragOff.X));
                sel.Y = Math.Max(0, Math.Min(Render.S, p.Y - dragOff.Y));
                LayoutChanged(false);
                return;
            }
            if (tab == 2 && rPreview.Contains(e.Location)) { Cursor = Cursors.Cross; Invalidate(rPreview); }
            if (tab == 3 && rPreview.Contains(e.Location))
            {
                int hc = HandleAt(Local(e.Location));
                Cursor = hc == 0 || hc == 3 ? Cursors.SizeNWSE : hc >= 0 ? Cursors.SizeNESW : layout.HitTest(Local(e.Location)) != null ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            var h = HitTest(e.Location);
            if (h != hover) { hover = h; if (!(tab == 2 && rPreview.Contains(e.Location))) Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != null) { hover = null; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (rPreview.Contains(e.Location))
            {
                if (tab == 2) { PushUndo(); drawing = true; Capture = true; lastPt = e.Location; StrokeTo(e.Location); return; }
                if (tab == 3)
                {
                    var p = Local(e.Location);
                    if (HandleAt(p) >= 0)
                    {
                        resizeW = true; Capture = true; resizeStartScale = sel.Scale;
                        resizeStartDist = (float)Math.Sqrt((p.X - sel.X) * (p.X - sel.X) + (p.Y - sel.Y) * (p.Y - sel.Y));
                        return;
                    }
                    sel = layout.HitTest(p);
                    if (sel != null) { dragW = true; Capture = true; dragOff = new PointF(p.X - sel.X, p.Y - sel.Y); }
                    if (txtEdit.Visible) txtEdit.Visible = false;
                    Invalidate(); Focus();
                    return;
                }
            }
            if (rSlider.Contains(e.Location) && tab == 4) { dragSlider = true; Capture = true; SetSlider(e.X); return; }
            var h = HitTest(e.Location);
            if (h != null) { if (h.Click != null) h.Click(); return; }
            if (e.Y < TitleH) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragSlider) { dragSlider = false; Capture = false; cfg.Save(); }
            if (drawing) { drawing = false; Capture = false; CanvasChanged(true); }
            if (dragW) { dragW = false; Capture = false; LayoutChanged(true); }
            if (resizeW) { resizeW = false; Capture = false; LayoutChanged(true); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (tab != 3 || !rPreview.Contains(e.Location)) return;
            var w = layout.HitTest(Local(e.Location)) ?? sel;
            if (w == null) return;
            sel = w;
            w.Scale = Math.Max(0.3f, Math.Min(4f, w.Scale + (e.Delta > 0 ? 0.1f : -0.1f)));
            LayoutChanged(true);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!txtEdit.Focused)
            {
                if (keyData == Keys.Delete && tab == 3 && sel != null) { DeleteSel(); return true; }
                if (keyData == (Keys.Control | Keys.Z) && tab == 2) { Undo(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void SetSlider(int mx)
        {
            int v = 1 + (int)Math.Round(99.0 * Math.Max(0, Math.Min(1, (mx - rSlider.X) / (double)rSlider.Width)));
            if (v == cfg.Brightness) return;
            cfg.Brightness = v;
            engine.Apply(cfg);
            Invalidate(new Rectangle(rSlider.X - 20, rSlider.Y - 34, rSlider.Width + 40, 64));
        }

        // ================= действия =================

        string[] rivals = new string[0];

        void ToggleStopAll()
        {
            if (engine.Paused) { engine.SetPaused(false); Engine.Log("вывод запущен вручную"); }
            else
            {
                int k = Engine.KillRivals();
                engine.SetPaused(true);
                Engine.Log("остановить всё: закрыто программ " + k);
            }
            rivals = Engine.FindRivals();
            Invalidate();
        }

        void SetTab(int i)
        {
            tab = i;
            if (i == 0 && cfg.Mode != Mode.Dashboard) { cfg.Mode = Mode.Dashboard; Changed(); }
            if (i == 1 && media != null && cfg.Mode != Mode.Media) { cfg.Mode = Mode.Media; Changed(); }
            if (i == 2 && cfg.Mode != Mode.Drawing) { cfg.Mode = Mode.Drawing; Changed(); }
            if (i == 3 && cfg.Mode != Mode.Custom) { cfg.Mode = Mode.Custom; Changed(); }
            if (i == 0) MakeThumbs();
            Invalidate();
        }

        void Changed()
        {
            cfg.Save();
            engine.Apply(cfg);
            Invalidate();
        }

        DateTime lastLayoutPush;
        void LayoutChanged(bool final)
        {
            Invalidate(rPreview);
            if (final) { layout.Save(); engine.SetLayout(layout); Invalidate(); lastLayoutPush = DateTime.Now; }
            else if ((DateTime.Now - lastLayoutPush).TotalMilliseconds > 150) { engine.SetLayout(layout); lastLayoutPush = DateTime.Now; }
        }

        void AddWidget(WKind k)
        {
            var w = new Widget { Kind = k, X = 160, Y = 160 };
            if (k == WKind.Text) w.Text = "Мой ПК";
            layout.Items.Add(w);
            sel = w;
            LayoutChanged(true);
            if (k == WKind.Text) BeginInvoke((Action)(() => { txtEdit.Focus(); txtEdit.SelectAll(); }));
        }

        void DeleteSel()
        {
            if (sel == null) return;
            layout.Items.Remove(sel); sel = null;
            txtEdit.Visible = false;
            LayoutChanged(true);
        }

        void PickBg(int i)
        {
            if (i == 0) cfg.CustomBg = "";
            else if (i == 1)
            {
                using (var d = new ColorDialog { FullOpen = true, Color = Color.FromArgb(10, 12, 20) })
                    if (d.ShowDialog(this) == DialogResult.OK) cfg.CustomBg = Settings.Hex(d.Color); else return;
            }
            else
            {
                using (var d = new OpenFileDialog { Filter = "Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = "Фон для своего дашборда" })
                    if (d.ShowDialog(this) == DialogResult.OK) cfg.CustomBg = d.FileName; else return;
            }
            Changed();
        }

        // --- рисовалка ---
        static Bitmap LoadCanvas()
        {
            var c = new Bitmap(Render.S, Render.S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(c))
            {
                g.Clear(Color.Black);
                try
                {
                    if (File.Exists(Settings.DrawingPath))
                        using (var ms = new MemoryStream(File.ReadAllBytes(Settings.DrawingPath)))
                        using (var img = Image.FromStream(ms)) g.DrawImage(img, 0, 0, Render.S, Render.S);
                }
                catch { }
            }
            return c;
        }

        int BrushPx() { return brushSize == 0 ? 4 : brushSize == 1 ? 10 : 24; }

        void StrokeTo(Point p)
        {
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var col = eraser ? Color.Black : brush;
                int w = eraser ? BrushPx() * 2 : BrushPx();
                var a = Local(lastPt); var b = Local(p);
                using (var pen = new Pen(col, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (a == b) using (var br = new SolidBrush(col)) g.FillEllipse(br, b.X - w / 2f, b.Y - w / 2f, w, w);
                    else g.DrawLine(pen, a, b);
                }
            }
            lastPt = p;
            Invalidate(rPreview);
            if ((DateTime.Now - lastPush).TotalMilliseconds > 120) CanvasChanged(false);
        }

        void CanvasChanged(bool save)
        {
            lastPush = DateTime.Now;
            engine.SetDrawing((Bitmap)canvas.Clone());
            if (save)
            {
                try { Directory.CreateDirectory(Settings.Dir); canvas.Save(Settings.DrawingPath, ImageFormat.Png); } catch { }
                Invalidate();
            }
        }

        void PushUndo()
        {
            undo.Add((Bitmap)canvas.Clone());
            if (undo.Count > 20) { undo[0].Dispose(); undo.RemoveAt(0); }
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            var b = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
            Swap(ref canvas, b);
            CanvasChanged(true);
        }

        void FillCanvas()
        {
            PushUndo();
            using (var g = Graphics.FromImage(canvas)) g.Clear(brush);
            CanvasChanged(true);
        }

        void LoadImageToCanvas()
        {
            using (var d = new OpenFileDialog { Filter = "Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = "Картинка под рисунок" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    PushUndo();
                    using (var ms = new MemoryStream(File.ReadAllBytes(d.FileName)))
                    using (var img = Image.FromStream(ms))
                    using (var g = Graphics.FromImage(canvas)) Render.DrawFitted(g, img, Render.Fit.Cover);
                    CanvasChanged(true);
                }
                catch { MessageBox.Show(this, "Не получилось открыть картинку.", "Рисунок"); }
            }
        }

        // --- GIF ---
        void LoadMedia()
        {
            if (media != null) { ImageAnimator.StopAnimate(media, OnGifFrame); media.Dispose(); media = null; }
            if (string.IsNullOrEmpty(cfg.MediaPath) || !File.Exists(cfg.MediaPath)) return;
            try
            {
                media = Image.FromStream(new MemoryStream(File.ReadAllBytes(cfg.MediaPath)));
                if (ImageAnimator.CanAnimate(media)) ImageAnimator.Animate(media, OnGifFrame);
            }
            catch { media = null; }
        }

        void OnGifFrame(object o, EventArgs e) { if (Visible && cfg.Mode == Mode.Media) Ui(() => Invalidate(rPreview), null); }

        void PickFile()
        {
            using (var d = new OpenFileDialog { Filter = "GIF и картинки|*.gif;*.png;*.jpg;*.jpeg;*.bmp|Все файлы|*.*", Title = "GIF или картинка для экрана" })
                if (d.ShowDialog(this) == DialogResult.OK) SetFile(d.FileName);
        }

        void SetFile(string f)
        {
            cfg.MediaPath = f;
            cfg.Mode = Mode.Media;
            tab = 1;
            LoadMedia();
            Swap(ref prevOverlay, null);
            Changed();
        }

        void SetAccent(Color c)
        {
            cfg.Accent = c;
            thumbs = null;
            Changed();
        }

        void ShowWin()
        {
            ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate();
            engine.PreviewWanted = true;
            if (media != null && ImageAnimator.CanAnimate(media)) ImageAnimator.Animate(media, OnGifFrame);
        }

        void HideWin()
        {
            Hide();
            engine.PreviewWanted = false;
            if (media != null && ImageAnimator.CanAnimate(media)) ImageAnimator.StopAnimate(media, OnGifFrame);
            Swap(ref prevFrame, null); Swap(ref prevOverlay, null);
            if (thumbs != null) { for (int i = 0; i < thumbs.Length; i++) Swap(ref thumbs[i], null); thumbs = null; }
            foreach (var u in undo) u.Dispose();
            undo.Clear();
            GC.Collect();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && Visible) { WindowState = FormWindowState.Normal; HideWin(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideWin(); return; }
            tray.Visible = false;
            engine.Stop();
            base.OnFormClosing(e);
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool AutostartOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("CoolerScreen") != null;
        }
        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (on) k.SetValue("CoolerScreen", "\"" + Application.ExecutablePath + "\" /tray");
                else k.DeleteValue("CoolerScreen", false);
        }

        static Icon AppIcon()
        {
            try { var i = Icon.ExtractAssociatedIcon(Application.ExecutablePath); if (i != null) return i; } catch { }
            return MakeIcon(Color.FromArgb(0, 200, 255));
        }

        static Icon MakeIcon(Color accent)
        {
            using (var b = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var br = new SolidBrush(Color.FromArgb(16, 18, 26))) g.FillEllipse(br, 1, 1, 30, 30);
                    using (var p = new Pen(accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, 6, 6, 20, 20, 135, 220);
                    using (var br = new SolidBrush(accent)) g.FillEllipse(br, 13, 13, 6, 6);
                }
                return Icon.FromHandle(b.GetHicon());
            }
        }
    }
}
