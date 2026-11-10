using System;
using System.Threading;
using System.Windows.Forms;

namespace CoolerScreen
{
    static class Program
    {
        public static EventWaitHandle ShowSignal, QuitSignal;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PowerThrottling { public uint Version, ControlMask, StateMask; }
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);

        // Windows 11 урезает скорость свёрнутым программам (EcoQoS) — экрану это вредит, отключаем
        static void NoThrottling()
        {
            try
            {
                var s = new PowerThrottling { Version = 1, ControlMask = 1 | 4, StateMask = 0 };   // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION
                SetProcessInformation(GetCurrentProcess(), 4, ref s, 12);
                timeBeginPeriod(4);   // ровный темп кадров GIF
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "CoolerScreen.show", out created);
            bool created2;
            QuitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "CoolerScreen.quit", out created2);
            if (!created)
            {
                // уже запущена: «/quit» — аккуратно закрыть её (дописав текущий кадр), иначе — открыть окно
                if (Array.IndexOf(args, "/quit") >= 0) QuitSignal.Set(); else ShowSignal.Set();
                return;
            }
            if (Array.IndexOf(args, "/quit") >= 0) return;
            NoThrottling();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool hidden = Array.IndexOf(args, "/tray") >= 0;
            Application.Run(new MainForm(Settings.Load(), hidden));
        }
    }
}
