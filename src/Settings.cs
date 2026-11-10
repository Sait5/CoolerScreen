using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace CoolerScreen
{
    public enum Mode { Dashboard, Media, Drawing, Custom }

    public sealed class Settings
    {
        public Mode Mode = Mode.Dashboard;
        public int Theme = 0;
        public string MediaPath = "";
        public Render.Fit Fit = Render.Fit.Cover;
        public Color Accent = Color.FromArgb(0, 200, 255);
        public int Brightness = 100;
        public bool Overlay = true;
        public int Rotation = 0;        // 0..3 -> 0/90/180/270
        public bool NightDim = false;   // приглушать 23:00–08:00
        public string CustomBg = "";    // "" = тёмный градиент, "#RRGGBB" = цвет, иначе путь к картинке

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoolerScreen"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }
        public static string DrawingPath { get { return Path.Combine(Dir, "drawing.png"); } }
        public static string LayoutPath { get { return Path.Combine(Dir, "layout.txt"); } }

        public Settings Clone() { return (Settings)MemberwiseClone(); }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                string v; int n;
                if (d.TryGetValue("mode", out v)) s.Mode = v == "media" ? Mode.Media : v == "drawing" ? Mode.Drawing : v == "custom" ? Mode.Custom : Mode.Dashboard;
                if (d.TryGetValue("theme", out v) && int.TryParse(v, out n)) s.Theme = Math.Max(0, Math.Min(Themes.Names.Length - 1, n));
                if (d.TryGetValue("media", out v)) s.MediaPath = v;
                if (d.TryGetValue("fit", out v)) Enum.TryParse(v, out s.Fit);
                if (d.TryGetValue("accent", out v)) s.Accent = ColorTranslator.FromHtml(v);
                if (d.TryGetValue("brightness", out v) && int.TryParse(v, out n)) s.Brightness = Math.Max(1, Math.Min(100, n));
                if (d.TryGetValue("overlay", out v)) s.Overlay = v == "1";
                if (d.TryGetValue("rotation", out v) && int.TryParse(v, out n)) s.Rotation = n & 3;
                if (d.TryGetValue("night", out v)) s.NightDim = v == "1";
                if (d.TryGetValue("custombg", out v)) s.CustomBg = v;
            }
            catch { }
            return s;
        }

        public static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath,
                    "mode=" + Mode.ToString().ToLowerInvariant() + "\r\n" +
                    "theme=" + Theme + "\r\n" +
                    "media=" + MediaPath + "\r\n" +
                    "fit=" + Fit + "\r\n" +
                    "accent=" + Hex(Accent) + "\r\n" +
                    "brightness=" + Brightness + "\r\n" +
                    "overlay=" + (Overlay ? "1" : "0") + "\r\n" +
                    "rotation=" + Rotation + "\r\n" +
                    "night=" + (NightDim ? "1" : "0") + "\r\n" +
                    "custombg=" + CustomBg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }
}
