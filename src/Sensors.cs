using System;
using System.Runtime.InteropServices;

namespace CoolerScreen
{
    public struct SensorData
    {
        public float CpuLoad, RamLoad, RamUsedGb, RamTotalGb;
        public float GpuTemp, GpuHotspot, GpuLoad, GpuPower, GpuClock, GpuFan, VramMb;
        public bool HasGpu;
    }

    // ЦП и ОЗУ — через WinAPI, видеокарта AMD — через родной драйвер (ADL), без сторонних драйверов.
    public sealed class Sensors
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMSTAT { public uint len, load; public ulong total, avail, tpf, apf, tv, av, ae; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        delegate IntPtr AllocCb(int size);
        static readonly AllocCb alloc = s => Marshal.AllocHGlobal(s);
        [DllImport("atiadlxx.dll")] static extern int ADL2_Main_Control_Create(AllocCb cb, int enumConnected, out IntPtr ctx);
        [DllImport("atiadlxx.dll")] static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int n);
        [DllImport("atiadlxx.dll")] static extern int ADL2_New_QueryPMLogData_Get(IntPtr ctx, int idx, IntPtr outp);
        [DllImport("atiadlxx.dll")] static extern int ADL2_Adapter_VRAMUsage_Get(IntPtr ctx, int idx, out int mb);

        long lastIdle, lastTotal;
        IntPtr adl, pmBuf;
        int adapter = -1;
        const int PM_SIZE = 4 + 256 * 8;

        public Sensors()
        {
            long i, k, u;
            GetSystemTimes(out i, out k, out u);
            lastIdle = i; lastTotal = k + u;
            try
            {
                if (ADL2_Main_Control_Create(alloc, 1, out adl) == 0)
                {
                    pmBuf = Marshal.AllocHGlobal(PM_SIZE);
                    int n; ADL2_Adapter_NumberOfAdapters_Get(adl, out n);
                    for (int a = 0; a < n && adapter < 0; a++)
                    {
                        Marshal.WriteInt32(pmBuf, PM_SIZE);
                        if (ADL2_New_QueryPMLogData_Get(adl, a, pmBuf) == 0) adapter = a;
                    }
                }
            }
            catch { adapter = -1; }   // нет видеокарты AMD — просто без данных ГП
        }

        int Pm(int i, out bool ok)
        {
            ok = Marshal.ReadInt32(pmBuf, 4 + i * 8) != 0;
            return Marshal.ReadInt32(pmBuf, 8 + i * 8);
        }

        public SensorData Read()
        {
            var d = new SensorData();
            long idle, k, u;
            GetSystemTimes(out idle, out k, out u);
            long tot = k + u, dt = tot - lastTotal, di = idle - lastIdle;
            if (dt > 0) d.CpuLoad = Math.Max(0, Math.Min(100, 100f * (dt - di) / dt));
            lastIdle = idle; lastTotal = tot;

            var m = new MEMSTAT { len = (uint)Marshal.SizeOf(typeof(MEMSTAT)) };
            if (GlobalMemoryStatusEx(ref m))
            {
                d.RamTotalGb = m.total / 1073741824f;
                d.RamUsedGb = (m.total - m.avail) / 1073741824f;
                d.RamLoad = 100f * (m.total - m.avail) / m.total;
            }

            if (adapter >= 0)
            {
                try
                {
                    Marshal.WriteInt32(pmBuf, PM_SIZE);
                    if (ADL2_New_QueryPMLogData_Get(adl, adapter, pmBuf) == 0)
                    {
                        bool ok;
                        d.HasGpu = true;
                        d.GpuTemp = Pm(8, out ok);
                        d.GpuHotspot = Pm(27, out ok); if (!ok) d.GpuHotspot = d.GpuTemp;
                        d.GpuLoad = Pm(19, out ok);
                        d.GpuPower = Pm(23, out ok);
                        d.GpuClock = Pm(1, out ok);
                        d.GpuFan = Pm(14, out ok);
                        int vr; if (ADL2_Adapter_VRAMUsage_Get(adl, adapter, out vr) == 0) d.VramMb = vr;
                    }
                }
                catch { }
            }
            return d;
        }
    }
}
