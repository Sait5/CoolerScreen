using System;
using System.Runtime.InteropServices;

namespace CoolerScreen
{
    // Сжатие кадров встроенным в Windows кодировщиком H.264 (Media Foundation).
    // Каждый кадр — самостоятельный ключевой (как любит экран), но в ~10 раз меньше несжатого.
    public sealed class MfEncoder : IDisposable
    {
        readonly int w, h;
        IMFTransform mft;
        bool providesSamples;
        int outSize;
        long time;
        byte[] spsPps;

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType t);
        [DllImport("mfplat.dll")] static extern int MFCreateSample(out IMFSample s);
        [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int max, out IMFMediaBuffer b);

        static readonly Guid MT_MAJOR = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        static readonly Guid MT_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        static readonly Guid MT_INTERLACE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        static readonly Guid MT_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        static readonly Guid MT_PAR = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        static readonly Guid MT_SEQ_HEADER = new Guid("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
        static readonly Guid Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        static readonly Guid NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        static readonly Guid CLSID_H264Enc = new Guid("6ca50344-051a-4ded-9779-a43305165e35");
        static readonly Guid API_GOP = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        static readonly Guid API_LOWLAT = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        static readonly Guid API_RCMODE = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");
        static readonly Guid API_QUALITY = new Guid("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
        static readonly Guid API_FORCEKEY = new Guid("398c1b98-8353-475a-9ef2-8f265d260345");

        const int MF_E_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        const int MF_E_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        ICodecAPI codec;

        public MfEncoder(int width, int height, int quality)
        {
            w = width; h = height;
            int hr = MFStartup(0x00020070, 0);
            if (hr < 0) throw new Exception("MFStartup " + hr.ToString("X"));
            mft = (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_H264Enc));

            codec = mft as ICodecAPI;
            if (codec != null)
            {
                Set(API_RCMODE, (uint)3);          // режим «качество»
                Set(API_QUALITY, (uint)quality);
                Set(API_GOP, (uint)1);             // каждый кадр — ключевой
                Set(API_LOWLAT, (uint)1);
            }

            IMFMediaType o; MFCreateMediaType(out o);
            o.SetGUID(MT_MAJOR, Video); o.SetGUID(MT_SUBTYPE, H264);
            o.SetUINT32(MT_BITRATE, 8000000);
            o.SetUINT64(MT_FRAME_SIZE, ((long)w << 32) | (uint)h);
            o.SetUINT64(MT_FRAME_RATE, (30L << 32) | 1);
            o.SetUINT64(MT_PAR, (1L << 32) | 1);
            o.SetUINT32(MT_INTERLACE, 2);
            o.SetUINT32(MT_PROFILE, 66);           // Baseline
            Check(mft.SetOutputType(0, o, 0), "SetOutputType");

            IMFMediaType i; MFCreateMediaType(out i);
            i.SetGUID(MT_MAJOR, Video); i.SetGUID(MT_SUBTYPE, NV12);
            i.SetUINT64(MT_FRAME_SIZE, ((long)w << 32) | (uint)h);
            i.SetUINT64(MT_FRAME_RATE, (30L << 32) | 1);
            i.SetUINT64(MT_PAR, (1L << 32) | 1);
            i.SetUINT32(MT_INTERLACE, 2);
            Check(mft.SetInputType(0, i, 0), "SetInputType");

            ReadOutInfo();
            mft.ProcessMessage(0x10000000, IntPtr.Zero);   // NOTIFY_BEGIN_STREAMING
            mft.ProcessMessage(0x10000003, IntPtr.Zero);   // NOTIFY_START_OF_STREAM
        }

        void Set(Guid api, object v) { try { var g = api; codec.SetValue(ref g, ref v); } catch { } }
        static void Check(int hr, string what) { if (hr < 0) throw new Exception(what + " 0x" + hr.ToString("X8")); }

        void ReadOutInfo()
        {
            MFT_OUTPUT_STREAM_INFO info;
            mft.GetOutputStreamInfo(0, out info);
            providesSamples = (info.dwFlags & 0x300) != 0;
            outSize = Math.Max(info.cbSize, w * h * 2);
        }

        public byte[] Encode(Yuv f)
        {
            // I420 -> NV12
            int ySize = w * h, cSize = ySize / 4;
            IMFMediaBuffer buf; MFCreateMemoryBuffer(ySize + cSize * 2, out buf);
            IntPtr p; int max, cur;
            buf.Lock(out p, out max, out cur);
            Marshal.Copy(f.Y, 0, p, ySize);
            var uv = new byte[cSize * 2];
            for (int k = 0; k < cSize; k++) { uv[2 * k] = f.U[k]; uv[2 * k + 1] = f.V[k]; }
            Marshal.Copy(uv, 0, p + ySize, uv.Length);
            buf.Unlock();
            buf.SetCurrentLength(ySize + cSize * 2);

            IMFSample s; MFCreateSample(out s);
            s.AddBuffer(buf);
            s.SetSampleTime(time); s.SetSampleDuration(333333); time += 333333;
            if (codec != null) Set(API_FORCEKEY, (uint)1);
            Check(mft.ProcessInput(0, s, 0), "ProcessInput");
            Marshal.ReleaseComObject(s); Marshal.ReleaseComObject(buf);
            mft.ProcessMessage(0x00000001, IntPtr.Zero);   // COMMAND_DRAIN — отдать кадр сразу, без задержки

            byte[] result = null;
            for (int guard = 0; guard < 8; guard++)
            {
                var od = new MFT_OUTPUT_DATA_BUFFER[1];
                IMFSample outS = null;
                if (!providesSamples)
                {
                    IMFMediaBuffer ob; MFCreateMemoryBuffer(outSize, out ob);
                    MFCreateSample(out outS); outS.AddBuffer(ob); Marshal.ReleaseComObject(ob);
                    od[0].pSample = Marshal.GetComInterfaceForObject(outS, typeof(IMFSample));
                }
                int status;
                int hr = mft.ProcessOutput(0, 1, od, out status);
                if (hr == MF_E_STREAM_CHANGE)
                {
                    IMFMediaType t; mft.GetOutputAvailableType(0, 0, out t);
                    mft.SetOutputType(0, t, 0); ReadOutInfo();
                    Free(od[0], outS); continue;
                }
                if (hr == MF_E_NEED_MORE_INPUT) { Free(od[0], outS); break; }
                Check(hr, "ProcessOutput");
                var got = (IMFSample)Marshal.GetObjectForIUnknown(od[0].pSample);
                IMFMediaBuffer cb; got.ConvertToContiguousBuffer(out cb);
                IntPtr q; int m2, len;
                cb.Lock(out q, out m2, out len);
                var bytes = new byte[len]; Marshal.Copy(q, bytes, 0, len);
                cb.Unlock();
                Marshal.ReleaseComObject(cb);
                Marshal.ReleaseComObject(got);
                Free(od[0], outS);
                result = result == null ? bytes : Concat(result, bytes);
            }
            if (result == null) return null;
            return WithHeaders(Strip(result));
        }

        static void Free(MFT_OUTPUT_DATA_BUFFER d, IMFSample mine)
        {
            if (d.pSample != IntPtr.Zero) Marshal.Release(d.pSample);
            if (d.pEvents != IntPtr.Zero) Marshal.Release(d.pEvents);
            if (mine != null) Marshal.ReleaseComObject(mine);
        }

        // Убираем служебные NAL (AUD=9, SEI=6): экрану нужны только SPS, PPS и сам кадр
        static byte[] Strip(byte[] d)
        {
            var ms = new System.IO.MemoryStream(d.Length);
            int i = 0, n = d.Length, start = -1;
            Action<int> flush = end => { };
            var starts = new System.Collections.Generic.List<int>();
            for (int k = 0; k + 2 < n; k++) if (d[k] == 0 && d[k + 1] == 0 && d[k + 2] == 1) { starts.Add(k + 3); k += 2; }
            for (int s = 0; s < starts.Count; s++)
            {
                int b = starts[s], e = s + 1 < starts.Count ? starts[s + 1] - 3 : n;
                while (e > b && d[e - 1] == 0) e--;
                int type = d[b] & 0x1F;
                if (type == 9 || type == 6) continue;
                ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1);
                ms.Write(d, b, e - b);
            }
            return ms.ToArray();
        }

        static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, r, 0, a.Length); Buffer.BlockCopy(b, 0, r, a.Length, b.Length); return r; }

        static bool HasNal(byte[] d, int type)
        {
            for (int i = 0; i + 3 < d.Length; i++)
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1 && (d[i + 3] & 0x1F) == type) return true;
            return false;
        }

        // Экрану нужен самостоятельный кадр: SPS + PPS + IDR. Если кодировщик их не повторил — добавляем.
        byte[] WithHeaders(byte[] frame)
        {
            if (HasNal(frame, 7)) { spsPps = Extract(frame); return frame; }
            if (spsPps == null)
            {
                try
                {
                    IMFMediaType t; mft.GetOutputCurrentType(0, out t);
                    int size; t.GetBlobSize(MT_SEQ_HEADER, out size);
                    var blob = new byte[size]; int got; t.GetBlob(MT_SEQ_HEADER, blob, size, out got);
                    spsPps = blob;
                }
                catch { }
            }
            return spsPps != null ? Concat(spsPps, frame) : frame;
        }

        // SPS+PPS из начала кадра (до первого среза)
        static byte[] Extract(byte[] d)
        {
            for (int i = 0; i + 3 < d.Length; i++)
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
                {
                    int t = d[i + 3] & 0x1F;
                    if (t == 5 || t == 1)
                    {
                        int start = (i > 0 && d[i - 1] == 0) ? i - 1 : i;
                        var r = new byte[start]; Buffer.BlockCopy(d, 0, r, 0, start); return r;
                    }
                }
            return null;
        }

        public void Dispose()
        {
            if (mft != null) { try { Marshal.ReleaseComObject(mft); } catch { } mft = null; }
        }

        // ---------- COM-интерфейсы Media Foundation ----------
        [StructLayout(LayoutKind.Sequential)]
        struct MFT_OUTPUT_STREAM_INFO { public int dwFlags, cbSize, cbAlignment; }
        [StructLayout(LayoutKind.Sequential)]
        struct MFT_OUTPUT_DATA_BUFFER { public int dwStreamID; public IntPtr pSample; public int dwStatus; public IntPtr pEvents; }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaType
        {
            void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10();
            void GetBlobSize([In] ref Guid key, out int size);
            void GetBlob([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buf, int size, out int got);
            void _13(); void _14(); void _15(); void _16(); void _17();
            void SetUINT32([In] ref Guid key, int v);
            void SetUINT64([In] ref Guid key, long v);
            void _20();
            void SetGUID([In] ref Guid key, [In] ref Guid v);
            void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSample
        {
            void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
            void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
            void _20(); void _21(); void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
            void GetSampleFlags(out int f);
            void SetSampleFlags(int f);
            void GetSampleTime(out long t);
            void SetSampleTime(long t);
            void GetSampleDuration(out long d);
            void SetSampleDuration(long d);
            void GetBufferCount(out int n);
            void GetBufferByIndex(int i, out IMFMediaBuffer b);
            void ConvertToContiguousBuffer(out IMFMediaBuffer b);
            void AddBuffer(IMFMediaBuffer b);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaBuffer
        {
            void Lock(out IntPtr p, out int max, out int cur);
            void Unlock();
            void GetCurrentLength(out int len);
            void SetCurrentLength(int len);
            void GetMaxLength(out int len);
        }

        [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFTransform
        {
            void _GetStreamLimits(); void _GetStreamCount(); void _GetStreamIDs(); void _GetInputStreamInfo();
            void GetOutputStreamInfo(int id, out MFT_OUTPUT_STREAM_INFO info);
            void _GetAttributes(); void _GetInputStreamAttributes(); void _GetOutputStreamAttributes();
            void _DeleteInputStream(); void _AddInputStreams(); void _GetInputAvailableType();
            [PreserveSig] int GetOutputAvailableType(int id, int index, out IMFMediaType t);
            [PreserveSig] int SetInputType(int id, IMFMediaType t, int flags);
            [PreserveSig] int SetOutputType(int id, IMFMediaType t, int flags);
            void _GetInputCurrentType();
            void GetOutputCurrentType(int id, out IMFMediaType t);
            void _GetInputStatus(); void _GetOutputStatus(); void _SetOutputBounds(); void _ProcessEvent();
            [PreserveSig] int ProcessMessage(int msg, IntPtr param);
            [PreserveSig] int ProcessInput(int id, IMFSample s, int flags);
            [PreserveSig] int ProcessOutput(int flags, int count, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] MFT_OUTPUT_DATA_BUFFER[] bufs, out int status);
        }

        [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICodecAPI
        {
            void _IsSupported(); void _IsModifiable(); void _GetParameterRange(); void _GetParameterValues();
            void _GetDefaultValue(); void _GetValue();
            void SetValue([In] ref Guid api, [In] ref object value);
        }
    }
}
