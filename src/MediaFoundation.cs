// LUM - interop Media Foundation (API native Windows 7+) : capture caméra, encodage MP4 (H.264 + AAC).
// Compatible C# 5.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lum
{
    internal static class MF
    {
        public const uint FIRST_VIDEO_STREAM = 0xFFFFFFFC;

        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new Guid("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new Guid("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new Guid("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
        public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        // --- encodage (enregistrement d'écran)
        public static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");
        public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        public static readonly Guid IID_IMFMediaSource = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");

        private static bool _started;
        private static readonly object _lock = new object();

        public static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_started) return;
                Check(MFStartup(0x00020070, 0));
                _started = true;
            }
        }

        public static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        public static IMFActivate[] EnumVideoDevices()
        {
            IMFAttributes attrs;
            Check(MFCreateAttributes(out attrs, 1));
            Guid k = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, v = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            attrs.SetGUID(ref k, ref v);
            IntPtr arr; uint count;
            int hr = MFEnumDeviceSources(attrs, out arr, out count);
            Marshal.ReleaseComObject(attrs);
            Check(hr);
            var result = new IMFActivate[count];
            for (int i = 0; i < count; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                result[i] = (IMFActivate)Marshal.GetObjectForIUnknown(p);
                Marshal.Release(p);
            }
            if (arr != IntPtr.Zero) Marshal.FreeCoTaskMem(arr);
            return result;
        }

        public static IMFMediaType NewType(Guid major, Guid subtype)
        {
            IMFMediaType t;
            Check(MFCreateMediaType(out t));
            Guid k = MF_MT_MAJOR_TYPE; Check(t.SetGUID(ref k, ref major));
            k = MF_MT_SUBTYPE; Check(t.SetGUID(ref k, ref subtype));
            return t;
        }

        public static void Set32(IMFMediaType t, Guid key, int value) { Check(t.SetUINT32(ref key, value)); }
        public static void Set64(IMFMediaType t, Guid key, uint hi, uint lo) { Check(t.SetUINT64(ref key, ((ulong)hi << 32) | lo)); }

        public static string GetString(IMFActivate a, Guid key)
        {
            IntPtr p; uint len;
            if (a.GetAllocatedString(ref key, out p, out len) < 0) return null;
            string s = Marshal.PtrToStringUni(p, (int)len);
            Marshal.FreeCoTaskMem(p);
            return s;
        }

        // Classe les formats natifs : d'abord >= 24 i/s, puis la plus grande résolution <= 1280 px.
        public static List<uint> RankNativeTypes(IMFSourceReader reader)
        {
            var scored = new List<KeyValuePair<long, uint>>();
            for (uint i = 0; i < 300; i++)
            {
                IMFMediaType t;
                if (reader.GetNativeMediaType(FIRST_VIDEO_STREAM, i, out t) < 0 || t == null) break;
                ulong fsz; Guid fs = MF_MT_FRAME_SIZE;
                if (t.GetUINT64(ref fs, out fsz) >= 0)
                {
                    int w = (int)(fsz >> 32), h = (int)(fsz & 0xFFFFFFFF);
                    ulong fr; Guid frk = MF_MT_FRAME_RATE; double fps = 30;
                    if (t.GetUINT64(ref frk, out fr) >= 0 && (fr & 0xFFFFFFFF) != 0) fps = (double)(fr >> 32) / (fr & 0xFFFFFFFF);
                    long score = (w <= 1280 && h <= 1280) ? (long)w * h : -(long)w * h;
                    if (fps >= 24) score += 1L << 40; // on privilégie toujours la fluidité
                    scored.Add(new KeyValuePair<long, uint>(score, i));
                }
                Marshal.ReleaseComObject(t);
            }
            scored.Sort(delegate(KeyValuePair<long, uint> x, KeyValuePair<long, uint> y) { return y.Key.CompareTo(x.Key); });
            var result = new List<uint>();
            for (int i = 0; i < scored.Count && i < 8; i++) result.Add(scored[i].Value);
            return result;
        }

        public static bool TrySetRgb32(IMFSourceReader reader)
        {
            IMFMediaType rgb;
            if (MFCreateMediaType(out rgb) < 0) return false;
            Guid k = MF_MT_MAJOR_TYPE, v = MFMediaType_Video; rgb.SetGUID(ref k, ref v);
            k = MF_MT_SUBTYPE; v = MFVideoFormat_RGB32; rgb.SetGUID(ref k, ref v);
            int hr = reader.SetCurrentMediaType(FIRST_VIDEO_STREAM, IntPtr.Zero, rgb);
            Marshal.ReleaseComObject(rgb);
            return hr >= 0;
        }

        [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attrs, uint initialSize);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mf.dll")] public static extern int MFEnumDeviceSources(IMFAttributes attrs, out IntPtr activates, out uint count);
        [DllImport("mfreadwrite.dll")]
        public static extern int MFCreateSourceReaderFromMediaSource(
            [MarshalAs(UnmanagedType.IUnknown)] object source, IMFAttributes attrs, out IMFSourceReader reader);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IMFAttributes attrs, out IMFSinkWriter writer);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);
    }


    // Les interfaces COM ci-dessous reproduisent exactement l'ordre des vtables Windows.
    // Les méthodes préfixées "_" ne sont pas utilisées : elles réservent juste leur slot.
    // (C# ne sait pas hériter d'une vtable COM : chaque interface redéclare IMFAttributes.)

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void _GetDouble(); void _GetGUID(); void _GetStringLength(); void _GetString();
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void _SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void _SetString(); void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore();
        void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void _GetDouble(); void _GetGUID(); void _GetStringLength(); void _GetString();
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void _SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void _SetString(); void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore();
        void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
        // IMFMediaType
        void _GetMajorType(); void _IsCompressedFormat(); void _IsEqual(); void _GetRepresentation(); void _FreeRepresentation();
    }

    [ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFActivate
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void _GetDouble(); void _GetGUID(); void _GetStringLength(); void _GetString();
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void _SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void _SetString(); void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore();
        void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
        // IMFActivate
        void ActivateObject(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        void ShutdownObject();
        void DetachObject();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void _GetDouble(); void _GetGUID(); void _GetStringLength(); void _GetString();
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void _SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void _SetString(); void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore();
        void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
        // IMFSample
        void _GetSampleFlags(); void _SetSampleFlags(); void _GetSampleTime();
        [PreserveSig] int SetSampleTime(long time);
        void _GetSampleDuration();
        [PreserveSig] int SetSampleDuration(long duration);
        void _GetBufferCount(); void _GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        void _RemoveBufferByIndex(); void _RemoveAllBuffers(); void _GetTotalLength(); void _CopyToBuffer();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        void _GetStreamSelection(); void _SetStreamSelection();
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType type);
        void _SetCurrentPosition();
        [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex,
                                     out uint streamFlags, out long timestamp, out IMFSample sample);
        void _Flush(); void _GetServiceForStream(); void _GetPresentationAttribute();
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType targetType, out uint streamIndex);
        [PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputType, IMFAttributes encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);
        void _SendStreamTick(); void _PlaceMarker(); void _NotifyEndOfSegment(); void _Flush();
        [PreserveSig] int DoFinalize();   // = IMFSinkWriter::Finalize
        void _GetServiceForStream(); void _GetStatistics();
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        void _GetCurrentLength();
        [PreserveSig] int SetCurrentLength(int length);
        void _GetMaxLength();
    }
}
