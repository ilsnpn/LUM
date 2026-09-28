// LUM - enregistrement de l'écran où se trouve la bulle, en MP4 (H.264 + AAC micro).
// Capture GDI (BitBlt) + encodage Media Foundation (SinkWriter) + micro via waveIn.
// Tout est natif Windows : aucune DLL externe. Compatible C# 5.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lum
{
    public sealed class ScreenRecorder
    {
        public const int MicDefault = -1;   // micro par défaut de Windows
        public const int MicNone = -2;      // pas de son

        private const int Fps = 30;
        private const long FrameDuration = 10000000L / Fps;   // unités de 100 ns
        private const int MaxW = 3840, MaxH = 2160;           // limite de l'encodeur H.264

        private readonly IntPtr _bubble;
        private readonly string _path;
        private readonly int _mic;
        private readonly bool _flip;
        private readonly Action<Graphics, Rectangle> _drawOverlay; // dessine la bulle (si exclue de la capture)

        private Thread _videoThread, _audioThread;
        private volatile bool _stop, _discard;
        private readonly ManualResetEvent _started = new ManualResetEvent(false);
        private Exception _startError;
        private Action<string, Exception> _onFinished;

        private readonly object _writerLock = new object();
        private IMFSinkWriter _writer;
        private uint _videoStream, _audioStream;
        private bool _hasAudio, _hwUsed;
        private WaveIn _wave;

        private readonly Stopwatch _clock = new Stopwatch();
        private readonly object _pauseLock = new object();
        private bool _paused;
        private long _pausedTicks, _pauseStartTicks;

        public ScreenRecorder(IntPtr bubbleHwnd, string path, int mic, bool flip, Action<Graphics, Rectangle> drawOverlay)
        {
            _bubble = bubbleHwnd; _path = path; _mic = mic; _flip = flip; _drawOverlay = drawOverlay;
        }

        public string FilePath { get { return _path; } }
        public bool HasAudio { get { return _hasAudio; } }
        public bool Paused { get { lock (_pauseLock) return _paused; } }

        /// Temps enregistré (pauses exclues), en unités de 100 ns.
        public long Position
        {
            get
            {
                lock (_pauseLock)
                {
                    long now = _clock.Elapsed.Ticks;
                    return (_paused ? _pauseStartTicks : now) - _pausedTicks;
                }
            }
        }

        /// Démarre l'enregistrement. Lève une exception si l'encodeur ne peut pas être créé.
        public void Start()
        {
            _videoThread = new Thread(VideoLoop);
            _videoThread.IsBackground = true;
            _videoThread.Name = "LUM record";
            _videoThread.SetApartmentState(ApartmentState.MTA);
            _videoThread.Start();
            if (!_started.WaitOne(10000)) throw new TimeoutException("L'encodeur vidéo ne répond pas.");
            if (_startError != null) throw _startError;
        }

        public void Pause()
        {
            lock (_pauseLock) { if (_paused) return; _paused = true; _pauseStartTicks = _clock.Elapsed.Ticks; }
        }

        public void Resume()
        {
            lock (_pauseLock) { if (!_paused) return; _pausedTicks += _clock.Elapsed.Ticks - _pauseStartTicks; _paused = false; }
        }

        /// Arrête (et supprime le fichier si discard). onFinished est appelé depuis un thread de fond.
        public void Stop(bool discard, Action<string, Exception> onFinished)
        {
            _discard = discard;
            _onFinished = onFinished;
            _stop = true;
        }

        // ================================================================ vidéo
        private void VideoLoop()
        {
            Exception error = null;
            IntPtr screenDc = IntPtr.Zero, capDc = IntPtr.Zero, capBmp = IntPtr.Zero, capOld = IntPtr.Zero;
            IntPtr encDc = IntPtr.Zero, encBmp = IntPtr.Zero, encOld = IntPtr.Zero;
            try
            {
                // Ce thread travaille en pixels physiques, quel que soit le DPI de chaque écran.
                try { W.SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }

                IntPtr mon = W.MonitorFromWindow(_bubble, 2);
                var mi = new W.MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(W.MONITORINFO));
                if (!W.GetMonitorInfo(mon, ref mi)) throw new InvalidOperationException("Écran introuvable.");
                Rectangle screen = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);

                // Taille d'encodage : paire, et réduite si l'écran dépasse la 4K.
                double scale = Math.Min(1.0, Math.Min((double)MaxW / screen.Width, (double)MaxH / screen.Height));
                int ew = ((int)(screen.Width * scale)) & ~1, eh = ((int)(screen.Height * scale)) & ~1;
                bool scaled = ew != screen.Width || eh != screen.Height;

                screenDc = W.GetDC(IntPtr.Zero);
                IntPtr capBits;
                capDc = W.CreateCompatibleDC(screenDc);
                capBmp = CreateDib(screenDc, screen.Width, screen.Height, out capBits);
                capOld = W.SelectObject(capDc, capBmp);
                IntPtr encBits = capBits;
                if (scaled)
                {
                    encDc = W.CreateCompatibleDC(screenDc);
                    encBmp = CreateDib(screenDc, ew, eh, out encBits);
                    encOld = W.SelectObject(encDc, encBmp);
                    W.SetStretchBltMode(encDc, 4 /*HALFTONE*/);
                    W.SetBrushOrgEx(encDc, 0, 0, IntPtr.Zero);
                }
                int frameBytes = ew * eh * 4;

                OpenWriter(ew, eh);
                _clock.Start();
                _started.Set();

                long lastTs = -1;
                long next = 0;
                while (!_stop)
                {
                    if (Paused) { Thread.Sleep(15); next = _clock.Elapsed.Ticks; continue; }

                    long ts = Position;
                    W.BitBlt(capDc, 0, 0, screen.Width, screen.Height, screenDc, screen.X, screen.Y, W.SRCCOPY | W.CAPTUREBLT);
                    DrawOverlayAndCursor(capDc, screen);
                    if (scaled) W.StretchBlt(encDc, 0, 0, ew, eh, capDc, 0, 0, screen.Width, screen.Height, W.SRCCOPY);

                    if (ts > lastTs)
                    {
                        if (lastTs < 0)
                        {
                            // 1re image : si l'encodeur matériel la refuse, on repasse en logiciel.
                            try { WriteVideo(encBits, frameBytes, ew * 4, eh, ts); }
                            catch
                            {
                                if (!_hwUsed) throw;
                                lock (_writerLock) { Marshal.ReleaseComObject(_writer); _writer = null; }
                                try { File.Delete(_path); } catch { }
                                CreateWriter(ew, eh, false, _hasAudio);
                                WriteVideo(encBits, frameBytes, ew * 4, eh, ts);
                            }
                            if (_wave != null) StartAudio(); // le son démarre avec la 1re image
                        }
                        else WriteVideo(encBits, frameBytes, ew * 4, eh, ts);
                        lastTs = ts;
                    }

                    next += FrameDuration;
                    long wait = (next - _clock.Elapsed.Ticks) / 10000;
                    if (wait > 0) Thread.Sleep((int)wait);
                    else next = _clock.Elapsed.Ticks; // machine trop lente : on ne rattrape pas
                }
            }
            catch (Exception ex)
            {
                error = ex;
                if (!_started.WaitOne(0)) { _startError = ex; _started.Set(); }
            }
            finally
            {
                StopAudio();
                lock (_writerLock)
                {
                    if (_writer != null)
                    {
                        try { _writer.DoFinalize(); } catch (Exception ex) { if (error == null) error = ex; }
                        Marshal.ReleaseComObject(_writer);
                        _writer = null;
                    }
                }
                if (encDc != IntPtr.Zero) { W.SelectObject(encDc, encOld); W.DeleteObject(encBmp); W.DeleteDC(encDc); }
                if (capDc != IntPtr.Zero) { W.SelectObject(capDc, capOld); W.DeleteObject(capBmp); W.DeleteDC(capDc); }
                if (screenDc != IntPtr.Zero) W.ReleaseDC(IntPtr.Zero, screenDc);
            }

            if (_discard || (error != null && _startError != null))
            {
                try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            }
            var done = _onFinished;
            if (done != null && _startError == null) done(_discard ? null : _path, error);
        }

        private static IntPtr CreateDib(IntPtr dc, int w, int h, out IntPtr bits)
        {
            var bi = new W.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(W.BITMAPINFOHEADER));
            bi.biWidth = w; bi.biHeight = -h; // négatif = image de haut en bas
            bi.biPlanes = 1; bi.biBitCount = 32;
            IntPtr hbmp = W.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero) throw new OutOfMemoryException("Impossible d'allouer l'image de capture.");
            return hbmp;
        }

        private void DrawOverlayAndCursor(IntPtr dc, Rectangle screen)
        {
            if (_drawOverlay != null)
            {
                W.RECT r;
                if (W.GetWindowRect(_bubble, out r))
                {
                    var wr = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    if (wr.IntersectsWith(screen))
                    {
                        using (var g = Graphics.FromHdc(dc))
                        {
                            wr.Offset(-screen.X, -screen.Y);
                            try { _drawOverlay(g, wr); } catch { }
                        }
                    }
                }
            }

            var ci = new W.CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(W.CURSORINFO));
            if (W.GetCursorInfo(ref ci) && (ci.flags & 1) != 0 && ci.hCursor != IntPtr.Zero)
            {
                W.ICONINFO ii;
                int hx = 0, hy = 0;
                if (W.GetIconInfo(ci.hCursor, out ii))
                {
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) W.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) W.DeleteObject(ii.hbmColor);
                }
                W.DrawIconEx(dc, ci.ptScreenPos.x - hx - screen.X, ci.ptScreenPos.y - hy - screen.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
            }
        }

        // ================================================================ encodeur
        private void OpenWriter(int w, int h)
        {
            bool wantAudio = _mic != MicNone;
            if (wantAudio)
            {
                try { _wave = new WaveIn(_mic, 48000, 2); }
                catch { _wave = null; wantAudio = false; } // pas de micro : on enregistre sans son
            }

            // Essais successifs : accélération matérielle puis logiciel, avec puis sans son.
            Exception last = null;
            bool[][] attempts = { new[] { true, true }, new[] { false, true }, new[] { true, false }, new[] { false, false } };
            foreach (bool[] a in attempts)
            {
                bool hw = a[0], audio = a[1] && wantAudio;
                if (a[1] && !wantAudio) continue;
                try { CreateWriter(w, h, hw, audio); _hasAudio = audio; _hwUsed = hw; last = null; break; }
                catch (Exception ex)
                {
                    last = ex;
                    if (_writer != null) { Marshal.ReleaseComObject(_writer); _writer = null; }
                    try { if (File.Exists(_path)) File.Delete(_path); } catch { }
                }
            }
            if (last != null) { if (_wave != null) { _wave.Dispose(); _wave = null; } throw last; }
            if (!_hasAudio && _wave != null) { _wave.Dispose(); _wave = null; }
        }

        private void CreateWriter(int w, int h, bool hw, bool audio)
        {
            MF.EnsureStarted();
            Directory.CreateDirectory(Path.GetDirectoryName(_path));

            IMFAttributes attrs;
            MF.Check(MF.MFCreateAttributes(out attrs, 2));
            Guid k = MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
            attrs.SetUINT32(ref k, hw ? 1 : 0);
            try { MF.Check(MF.MFCreateSinkWriterFromURL(_path, IntPtr.Zero, attrs, out _writer)); }
            finally { Marshal.ReleaseComObject(attrs); }

            // --- vidéo H.264
            int bitrate = (int)Math.Max(2000000, Math.Min(16000000, (long)w * h * Fps / 12));
            IMFMediaType vOut = MF.NewType(MF.MFMediaType_Video, MF.MFVideoFormat_H264);
            MF.Set32(vOut, MF.MF_MT_AVG_BITRATE, bitrate);
            MF.Set32(vOut, MF.MF_MT_INTERLACE_MODE, 2);
            MF.Set64(vOut, MF.MF_MT_FRAME_SIZE, (uint)w, (uint)h);
            MF.Set64(vOut, MF.MF_MT_FRAME_RATE, Fps, 1);
            MF.Set64(vOut, MF.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            MF.Check(_writer.AddStream(vOut, out _videoStream));
            Marshal.ReleaseComObject(vOut);

            IMFMediaType vIn = MF.NewType(MF.MFMediaType_Video, MF.MFVideoFormat_RGB32);
            MF.Set32(vIn, MF.MF_MT_INTERLACE_MODE, 2);
            MF.Set64(vIn, MF.MF_MT_FRAME_SIZE, (uint)w, (uint)h);
            MF.Set64(vIn, MF.MF_MT_FRAME_RATE, Fps, 1);
            MF.Set64(vIn, MF.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            MF.Set32(vIn, MF.MF_MT_DEFAULT_STRIDE, _flip ? -w * 4 : w * 4);
            MF.Check(_writer.SetInputMediaType(_videoStream, vIn, null));
            Marshal.ReleaseComObject(vIn);

            // --- audio AAC (micro)
            if (audio)
            {
                IMFMediaType aOut = MF.NewType(MF.MFMediaType_Audio, MF.MFAudioFormat_AAC);
                MF.Set32(aOut, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                MF.Set32(aOut, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, _wave.SampleRate);
                MF.Set32(aOut, MF.MF_MT_AUDIO_NUM_CHANNELS, _wave.Channels);
                MF.Set32(aOut, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 20000); // 160 kbit/s
                MF.Check(_writer.AddStream(aOut, out _audioStream));
                Marshal.ReleaseComObject(aOut);

                IMFMediaType aIn = MF.NewType(MF.MFMediaType_Audio, MF.MFAudioFormat_PCM);
                MF.Set32(aIn, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                MF.Set32(aIn, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, _wave.SampleRate);
                MF.Set32(aIn, MF.MF_MT_AUDIO_NUM_CHANNELS, _wave.Channels);
                MF.Set32(aIn, MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, _wave.BlockAlign);
                MF.Set32(aIn, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, _wave.BytesPerSecond);
                MF.Set32(aIn, MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                MF.Check(_writer.SetInputMediaType(_audioStream, aIn, null));
                Marshal.ReleaseComObject(aIn);
            }

            MF.Check(_writer.BeginWriting());
        }

        private void WriteVideo(IntPtr bits, int length, int stride, int height, long ts)
        {
            WriteSample(_videoStream, bits, length, ts, FrameDuration);
        }

        private void WriteSample(uint stream, IntPtr data, int length, long ts, long duration)
        {
            IMFMediaBuffer buffer = null;
            IMFSample sample = null;
            try
            {
                MF.Check(MF.MFCreateMemoryBuffer(length, out buffer));
                IntPtr dst; int max, cur;
                MF.Check(buffer.Lock(out dst, out max, out cur));
                MF.CopyMemory(dst, data, new UIntPtr((uint)length));
                buffer.Unlock();
                MF.Check(buffer.SetCurrentLength(length));
                MF.Check(MF.MFCreateSample(out sample));
                MF.Check(sample.AddBuffer(buffer));
                sample.SetSampleTime(ts);
                sample.SetSampleDuration(duration);
                lock (_writerLock)
                {
                    if (_writer != null) MF.Check(_writer.WriteSample(stream, sample));
                }
            }
            finally
            {
                if (sample != null) Marshal.ReleaseComObject(sample);
                if (buffer != null) Marshal.ReleaseComObject(buffer);
            }
        }

        // ================================================================ audio
        private volatile bool _audioStop;

        private void StartAudio()
        {
            _audioThread = new Thread(AudioLoop);
            _audioThread.IsBackground = true;
            _audioThread.Name = "LUM audio";
            _audioThread.SetApartmentState(ApartmentState.MTA);
            _wave.Start();
            _audioThread.Start();
        }

        private void StopAudio()
        {
            _audioStop = true;
            if (_audioThread != null) { _audioThread.Join(2000); _audioThread = null; }
            if (_wave != null) { _wave.Dispose(); _wave = null; }
        }

        private void AudioLoop()
        {
            long nextTs = -1;
            try
            {
                while (!_audioStop)
                {
                    IntPtr data; int len;
                    int slot = _wave.TakeReady(out data, out len);
                    if (slot < 0) { Thread.Sleep(10); continue; }
                    try
                    {
                        if (len <= 0 || Paused) { nextTs = -1; continue; } // pendant la pause : on jette le son
                        long dur = (long)len * 10000000L / _wave.BytesPerSecond;
                        long expected = Math.Max(0, Position - dur);
                        // Continuité des horodatages, recalés si l'écart devient trop grand (pause, dérive).
                        if (nextTs < 0 || Math.Abs(expected - nextTs) > 2000000) nextTs = expected;
                        WriteSample(_audioStream, data, len, nextTs, dur);
                        nextTs += dur;
                    }
                    finally { _wave.Recycle(slot); }
                }
            }
            catch { /* le son s'arrête, la vidéo continue */ }
        }
    }

    // ==================================================================== micro (waveIn)
    internal sealed class WaveIn : IDisposable
    {
        public readonly int SampleRate, Channels, BlockAlign, BytesPerSecond;
        private IntPtr _h;
        private readonly IntPtr[] _headers;
        private readonly IntPtr[] _buffers;
        private readonly int _hdrSize = Marshal.SizeOf(typeof(W.WAVEHDR));
        private int _nextSlot;

        public WaveIn(int device, int sampleRate, int channels)
        {
            SampleRate = sampleRate; Channels = channels;
            BlockAlign = channels * 2; BytesPerSecond = sampleRate * BlockAlign;
            var fmt = new W.WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = (short)channels, nSamplesPerSec = sampleRate,
                nAvgBytesPerSec = BytesPerSecond, nBlockAlign = (short)BlockAlign, wBitsPerSample = 16, cbSize = 0
            };
            uint dev = device < 0 ? 0xFFFFFFFF : (uint)device;
            int r = W.waveInOpen(out _h, dev, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new InvalidOperationException("Micro indisponible (code " + r + ")");

            int bufBytes = BytesPerSecond / 10; // 100 ms
            _headers = new IntPtr[10];
            _buffers = new IntPtr[10];
            for (int i = 0; i < _headers.Length; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(bufBytes);
                var hdr = new W.WAVEHDR { lpData = _buffers[i], dwBufferLength = bufBytes };
                _headers[i] = Marshal.AllocHGlobal(_hdrSize);
                Marshal.StructureToPtr(hdr, _headers[i], false);
                W.waveInPrepareHeader(_h, _headers[i], _hdrSize);
                W.waveInAddBuffer(_h, _headers[i], _hdrSize);
            }
        }

        public void Start() { W.waveInStart(_h); }

        /// Renvoie l'indice d'un tampon rempli (dans l'ordre), ou -1.
        public int TakeReady(out IntPtr data, out int length)
        {
            data = IntPtr.Zero; length = 0;
            IntPtr p = _headers[_nextSlot];
            var hdr = (W.WAVEHDR)Marshal.PtrToStructure(p, typeof(W.WAVEHDR));
            if ((hdr.dwFlags & 1) == 0) return -1; // WHDR_DONE
            data = hdr.lpData; length = hdr.dwBytesRecorded;
            int slot = _nextSlot;
            _nextSlot = (_nextSlot + 1) % _headers.Length;
            return slot;
        }

        private static readonly int FlagsOffset = Marshal.OffsetOf(typeof(W.WAVEHDR), "dwFlags").ToInt32();

        public void Recycle(int slot)
        {
            IntPtr p = _headers[slot];
            Marshal.WriteInt32(p, FlagsOffset, Marshal.ReadInt32(p, FlagsOffset) & ~1); // efface WHDR_DONE
            W.waveInAddBuffer(_h, p, _hdrSize);
        }

        public static string[] ListDevices()
        {
            int n = W.waveInGetNumDevs();
            var names = new string[n];
            for (int i = 0; i < n; i++)
            {
                var caps = new W.WAVEINCAPS();
                names[i] = W.waveInGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(W.WAVEINCAPS))) == 0
                    ? caps.szPname : "Micro " + (i + 1);
            }
            return names;
        }

        public void Dispose()
        {
            if (_h == IntPtr.Zero) return;
            W.waveInReset(_h);
            for (int i = 0; i < _headers.Length; i++)
            {
                W.waveInUnprepareHeader(_h, _headers[i], _hdrSize);
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_buffers[i]);
            }
            W.waveInClose(_h);
            _h = IntPtr.Zero;
        }
    }

    // ==================================================================== Win32
    internal static class W
    {
        public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec;
            public short nBlockAlign, wBitsPerSample, cbSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser;
            public int dwFlags, dwLoops; public IntPtr lpNext, reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEINCAPS
        {
            public short wMid, wPid; public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public int dwFormats; public short wChannels, wReserved1;
        }

        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO ii);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, int usage, out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);

        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr h, uint device, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int waveInGetDevCaps(IntPtr id, ref WAVEINCAPS caps, int size);
    }
}
