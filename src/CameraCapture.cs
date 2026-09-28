// LUM - capture webcam via Media Foundation (API native Windows 7+, aucune dépendance).
// Code volontairement compatible C# 5 pour pouvoir être recompilé avec le csc.exe
// fourni d'office avec Windows (.NET Framework 4.x), sans rien installer.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lum
{
    public sealed class CameraCapture : IDisposable
    {
        public event Action<Bitmap> FrameReady;   // appelé sur le thread de capture
        public event Action<string> Error;        // appelé sur le thread de capture

        private Thread _thread;
        private volatile bool _running;
        private readonly int _deviceIndex;

        public CameraCapture(int deviceIndex)
        {
            _deviceIndex = deviceIndex;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "LUM capture";
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Dispose()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive) _thread.Join(1500);
            _thread = null;
        }

        // ---------------------------------------------------------------- listing
        public static List<string> ListCameras()
        {
            var names = new List<string>();
            MF.EnsureStarted();
            IMFActivate[] devices = MF.EnumVideoDevices();
            foreach (var d in devices)
            {
                names.Add(MF.GetString(d, MF.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? "Caméra");
                Marshal.ReleaseComObject(d);
            }
            return names;
        }

        // ---------------------------------------------------------------- boucle
        private void Run()
        {
            IMFActivate[] devices = null;
            IMFActivate device = null;
            object source = null;
            IMFSourceReader reader = null;
            try
            {
                MF.EnsureStarted();
                devices = MF.EnumVideoDevices();
                if (devices.Length == 0) { RaiseError("Aucune caméra détectée"); return; }
                int idx = (_deviceIndex >= 0 && _deviceIndex < devices.Length) ? _deviceIndex : 0;
                device = devices[idx];

                Guid iidSource = MF.IID_IMFMediaSource;
                device.ActivateObject(ref iidSource, out source);

                IMFAttributes attrs;
                MF.Check(MF.MFCreateAttributes(out attrs, 1));
                Guid vp = MF.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
                attrs.SetUINT32(ref vp, 1); // conversion YUY2/NV12/MJPG -> RGB32 par Windows
                MF.Check(MF.MFCreateSourceReaderFromMediaSource(source, attrs, out reader));
                Marshal.ReleaseComObject(attrs);

                // Essaie les résolutions natives de la meilleure à la moins bonne, jusqu'à ce que
                // Windows accepte de convertir le flux en RGB32. En dernier recours : format par défaut.
                bool ok = false;
                foreach (uint typeIndex in MF.RankNativeTypes(reader))
                {
                    IMFMediaType native;
                    if (reader.GetNativeMediaType(MF.FIRST_VIDEO_STREAM, typeIndex, out native) < 0) continue;
                    int hrN = reader.SetCurrentMediaType(MF.FIRST_VIDEO_STREAM, IntPtr.Zero, native);
                    Marshal.ReleaseComObject(native);
                    if (hrN < 0) continue;
                    if (MF.TrySetRgb32(reader)) { ok = true; break; }
                }
                if (!ok && !MF.TrySetRgb32(reader)) throw new InvalidOperationException("format vidéo non supporté");

                IMFMediaType cur;
                MF.Check(reader.GetCurrentMediaType(MF.FIRST_VIDEO_STREAM, out cur));
                ulong frameSize;
                Guid fs = MF.MF_MT_FRAME_SIZE;
                MF.Check(cur.GetUINT64(ref fs, out frameSize));
                int width = (int)(frameSize >> 32);
                int height = (int)(frameSize & 0xFFFFFFFF);
                int stride;
                Guid ds = MF.MF_MT_DEFAULT_STRIDE;
                if (cur.GetUINT32(ref ds, out stride) != 0) stride = width * 4;
                Marshal.ReleaseComObject(cur);

                while (_running)
                {
                    uint actualIndex, flags; long ts; IMFSample sample;
                    int hr = reader.ReadSample(MF.FIRST_VIDEO_STREAM, 0, out actualIndex, out flags, out ts, out sample);
                    if (hr < 0) { RaiseError("Caméra indisponible (utilisée par une autre application ?)"); break; }
                    if ((flags & 0x2) != 0) break; // fin de flux
                    if (sample == null) continue;
                    try
                    {
                        IMFMediaBuffer buffer;
                        MF.Check(sample.ConvertToContiguousBuffer(out buffer));
                        try
                        {
                            IntPtr data; int maxLen, curLen;
                            MF.Check(buffer.Lock(out data, out maxLen, out curLen));
                            try
                            {
                                Bitmap bmp = CopyFrame(data, curLen, width, height, stride);
                                var h = FrameReady;
                                if (bmp != null) { if (h != null) h(bmp); else bmp.Dispose(); }
                            }
                            finally { buffer.Unlock(); }
                        }
                        finally { Marshal.ReleaseComObject(buffer); }
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
            }
            catch (Exception ex)
            {
                RaiseError("Erreur caméra : " + ex.Message);
            }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                if (device != null) { try { device.ShutdownObject(); } catch { } }
                if (source != null) Marshal.ReleaseComObject(source);
                if (devices != null) foreach (var d in devices) Marshal.ReleaseComObject(d);
            }
        }

        private static Bitmap CopyFrame(IntPtr data, int length, int width, int height, int stride)
        {
            int rowBytes = width * 4;
            int absStride = Math.Abs(stride);
            if (absStride < rowBytes) absStride = rowBytes;
            if (length < absStride * (height - 1) + rowBytes) return null;

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    // stride négatif = image stockée de bas en haut
                    int srcRow = stride >= 0 ? y : (height - 1 - y);
                    IntPtr src = new IntPtr(data.ToInt64() + (long)srcRow * absStride);
                    IntPtr dst = new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride);
                    MF.CopyMemory(dst, src, new UIntPtr((uint)rowBytes));
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        private void RaiseError(string msg)
        {
            var h = Error;
            if (h != null) h(msg);
        }
    }
}
