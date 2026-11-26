using System.Runtime.InteropServices;

namespace ScreenShare.Media;

internal static class MfNative
{
    public const int MF_VERSION = 0x00020070;
    public const uint MFSTARTUP_NOSOCKET = 1;
    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    public const uint MFT_ENUM_FLAG_LOCALMFT = 0x00000010;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    public const uint MFT_ENUM_FLAG_ALL = 0x0000003F;

    public const int MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
    public const int MFT_MESSAGE_SET_D3D_MANAGER = 0x00000002;
    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
    public const int MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x00000200;
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    public const uint METransformNeedInput = 601;
    public const uint METransformHaveOutput = 602;

    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");
    public static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("D6C02D4B-6833-45B4-971A-05A4B04BAB91");
    public static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    public static readonly Guid CLSID_MSH265DecoderMFT = new("420A51A3-D605-430C-B4FC-45274FA6C562");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    public static readonly Guid MF_SA_D3D11_AWARE = new("206B4FC8-FCF9-4C51-AFE3-9764369E33A0");
    public static readonly Guid MF_TRANSFORM_ASYNC = new("F81A699A-649A-497D-8C73-29F8FED6AD7A");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("E5666D6B-3422-4EB6-A421-DA7DB1F8E207");
    public static readonly Guid MF_LOW_LATENCY = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
    public static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new("D7388766-18FE-48C6-A177-EE894867C8C4");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
    public static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3C036DE7-3AD0-4C9E-9216-EE6D6AC21CB3");
    public static readonly Guid MFSampleExtension_CleanPoint = new("9CDF01D8-A0F0-43BA-B077-EAA06CBD728A");

    public static readonly Guid IID_IMFMediaEventGenerator = new("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D");
    public static readonly Guid IID_IMFShutdown = new("97EC2EA4-0E42-4937-97AC-9D6D328824E1");
    public static readonly Guid IID_IMFDXGIBuffer = new("E7174CFA-1C9E-48B1-8866-626226BFC258");
    public static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");

    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    public static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
    public static readonly Guid CODECAPI_AVEncH264CABACEnable = new("EE6CAD62-D305-4248-A50E-E1B255F7CAFD");

    public static void EnsureInitialized()
    {
        MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    }

    public static long Pack2UInt32(uint high, uint low) => ((long)high << 32) | low;

    [DllImport("mfplat.dll")]
    public static extern int MFStartup(int version, uint dwFlags);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr ppDeviceManager);

    // ---- 原始 vtable 调用：输出样本走这里，避免 RCW 拖到 GC 才释放，占住解码器的显存样本池 ----
    // IUnknown 3 个槽 + IMFAttributes 30 个槽 = 33，派生接口方法从 33 开始。

    public static unsafe int QueryInterface(IntPtr unk, Guid iid, out IntPtr ppv)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)unk)[0])(unk, &iid, &result);
        ppv = hr >= 0 ? result : IntPtr.Zero;
        return hr;
    }

    public static unsafe uint AttrGetUInt32(IntPtr attributes, Guid key, uint fallback)
    {
        uint v;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)(*(void***)attributes)[7])(attributes, &key, &v);
        return hr >= 0 ? v : fallback;
    }

    public static unsafe long SampleTime(IntPtr sample)
    {
        long t;
        return ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)(*(void***)sample)[35])(sample, &t) >= 0 ? t : 0;
    }

    public static unsafe IntPtr SampleBuffer(IntPtr sample)
    {
        IntPtr buf;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)sample)[40])(sample, 0, &buf) >= 0 ? buf : IntPtr.Zero;
    }

    public static unsafe IntPtr SampleContiguousBuffer(IntPtr sample)
    {
        IntPtr buf;
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)sample)[41])(sample, &buf) >= 0 ? buf : IntPtr.Zero;
    }

    public static unsafe int BufferLock(IntPtr buffer, out IntPtr data, out uint length)
    {
        IntPtr p;
        uint max, cur;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, int>)(*(void***)buffer)[3])(buffer, &p, &max, &cur);
        data = p;
        length = cur;
        return hr;
    }

    public static unsafe void BufferUnlock(IntPtr buffer) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)buffer)[4])(buffer);

    public static unsafe int DxgiBufferGetTexture(IntPtr dxgiBuffer, out IntPtr texture, out uint subresource)
    {
        Guid iid = IID_ID3D11Texture2D;
        IntPtr tex;
        uint sub = 0;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)dxgiBuffer)[3])(dxgiBuffer, &iid, &tex);
        if (hr >= 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)(*(void***)dxgiBuffer)[4])(dxgiBuffer, &sub);
        }
        texture = hr >= 0 ? tex : IntPtr.Zero;
        subresource = sub;
        return hr;
    }

    public static unsafe int DeviceManagerReset(IntPtr manager, IntPtr device, uint token) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)(*(void***)manager)[7])(manager, device, token);

    /// <summary>IMFMediaEventGenerator::GetEvent，flags=0 时阻塞直到有事件或 MFT 关闭。</summary>
    public static unsafe int GetEvent(IntPtr generator, uint flags, out uint eventType, out int status)
    {
        IntPtr ev;
        eventType = 0;
        status = 0;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)generator)[3])(generator, flags, &ev);
        if (hr < 0) return hr;
        uint met;
        int st;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)(*(void***)ev)[33])(ev, &met);
        ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(void***)ev)[35])(ev, &st);
        Marshal.Release(ev);
        eventType = met;
        status = st;
        return hr;
    }

    public static unsafe void Shutdown(IntPtr shutdown) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)shutdown)[3])(shutdown);

    public static byte[] CopySampleBytes(IntPtr sample)
    {
        IntPtr buf = SampleContiguousBuffer(sample);
        if (buf == IntPtr.Zero) return [];
        try
        {
            if (BufferLock(buf, out var data, out var len) < 0) return [];
            try
            {
                var bytes = new byte[len];
                Marshal.Copy(data, bytes, 0, (int)len);
                return bytes;
            }
            finally
            {
                BufferUnlock(buf);
            }
        }
        finally
        {
            Marshal.Release(buf);
        }
    }

    /// <summary>枚举 MFT 并逐个激活，返回第一个通过 configure 的实例；configure 抛异常就换下一个。</summary>
    public static T? EnumerateAndActivate<T>(Guid category, uint flags, Guid inSubtype, Guid outSubtype, Func<IMFActivate, IMFTransform, T> configure)
        where T : class
    {
        var inInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = inSubtype };
        var outInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = outSubtype };
        if (MFTEnumEx(ref category, flags, ref inInfo, ref outInfo, out IntPtr pActivates, out uint count) < 0 || count == 0)
        {
            return null;
        }

        var activates = new List<IMFActivate>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = Marshal.ReadIntPtr(pActivates, i * IntPtr.Size);
                if (ptr != IntPtr.Zero) activates.Add((IMFActivate)Marshal.GetObjectForIUnknown(ptr));
            }
        }
        finally
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = Marshal.ReadIntPtr(pActivates, i * IntPtr.Size);
                if (ptr != IntPtr.Zero) Marshal.Release(ptr);
            }
            Marshal.FreeCoTaskMem(pActivates);
        }

        foreach (var activate in activates)
        {
            IMFTransform? mft = null;
            try
            {
                var iid = typeof(IMFTransform).GUID;
                activate.ActivateObject(ref iid, out IntPtr pMft);
                mft = (IMFTransform)Marshal.GetObjectForIUnknown(pMft);
                Marshal.Release(pMft);
                return configure(activate, mft);
            }
            catch
            {
                try { activate.ShutdownObject(); } catch { }
                if (mft is not null) Marshal.ReleaseComObject(mft);
            }
        }
        return null;
    }

    public static string? GetString(IMFAttributes attrs, Guid key)
    {
        try
        {
            attrs.GetAllocatedString(ref key, out IntPtr p, out _);
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? GetBlob(IMFAttributes attrs, Guid key)
    {
        try
        {
            attrs.GetAllocatedBlob(ref key, out IntPtr p, out uint size);
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(p, bytes, 0, (int)size);
                return bytes;
            }
            finally
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
        catch
        {
            return null;
        }
    }

    public static bool TrySetUInt32(IMFAttributes attrs, Guid key, uint value)
    {
        try { attrs.SetUINT32(ref key, value); return true; } catch { return false; }
    }

    public static uint GetUInt32(IMFAttributes attrs, Guid key, uint fallback)
    {
        try { attrs.GetUINT32(ref key, out var v); return v; } catch { return fallback; }
    }

    [DllImport("mfplat.dll")]
    public static extern int MFTEnumEx(
        [In] ref Guid guidCategory,
        uint flags,
        [In] ref MFT_REGISTER_TYPE_INFO pInputType,
        [In] ref MFT_REGISTER_TYPE_INFO pOutputType,
        out IntPtr pppMFTActivate,
        out uint pnumMFTActivate);

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        [In] ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        out IntPtr ppv);

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        public IntPtr pSample;
        public uint dwStatus;
        public IntPtr pEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public long val;
    }

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        void GetItem([In] ref Guid guidKey, IntPtr pValue);
        void GetItemType([In] ref Guid guidKey, out int pType);
        void CompareItem([In] ref Guid guidKey, IntPtr Value, out bool pbResult);
        void Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        void GetUINT32([In] ref Guid guidKey, out uint punValue);
        void GetUINT64([In] ref Guid guidKey, out ulong punValue);
        void GetDouble([In] ref Guid guidKey, out double pfValue);
        void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
        void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
        void GetString([In] ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
        void GetAllocatedString([In] ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
        void GetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
        void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
        void SetItem([In] ref Guid guidKey, IntPtr Value);
        void DeleteItem([In] ref Guid guidKey);
        void DeleteAllItems();
        void SetUINT32([In] ref Guid guidKey, uint unValue);
        void SetUINT64([In] ref Guid guidKey, ulong unValue);
        void SetDouble([In] ref Guid guidKey, double fValue);
        void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
        void SetString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        void SetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
        void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        void LockStore();
        void UnlockStore();
        void GetCount(out uint pcItems);
        void GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        void CopyAllItems(IMFAttributes pDest);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType : IMFAttributes
    {
        new void GetItem([In] ref Guid guidKey, IntPtr pValue);
        new void GetItemType([In] ref Guid guidKey, out int pType);
        new void CompareItem([In] ref Guid guidKey, IntPtr Value, out bool pbResult);
        new void Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        new void GetUINT32([In] ref Guid guidKey, out uint punValue);
        new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
        new void GetDouble([In] ref Guid guidKey, out double pfValue);
        new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
        new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
        new void GetString([In] ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
        new void GetAllocatedString([In] ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
        new void GetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
        new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
        new void SetItem([In] ref Guid guidKey, IntPtr Value);
        new void DeleteItem([In] ref Guid guidKey);
        new void DeleteAllItems();
        new void SetUINT32([In] ref Guid guidKey, uint unValue);
        new void SetUINT64([In] ref Guid guidKey, ulong unValue);
        new void SetDouble([In] ref Guid guidKey, double fValue);
        new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
        new void SetString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new void SetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
        new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new void LockStore();
        new void UnlockStore();
        new void GetCount(out uint pcItems);
        new void GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new void CopyAllItems(IMFAttributes pDest);
        void GetMajorType(out Guid pguidMajorType);
        void IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
        void IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
        void GetRepresentation([In] ref Guid guidRepresentation, out IntPtr ppvRepresentation);
        void FreeRepresentation([In] ref Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        void Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        void Unlock();
        void GetCurrentLength(out uint pcbCurrentLength);
        void SetCurrentLength(uint cbCurrentLength);
        void GetMaxLength(out uint pcbMaxLength);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample : IMFAttributes
    {
        new void GetItem([In] ref Guid guidKey, IntPtr pValue);
        new void GetItemType([In] ref Guid guidKey, out int pType);
        new void CompareItem([In] ref Guid guidKey, IntPtr Value, out bool pbResult);
        new void Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        new void GetUINT32([In] ref Guid guidKey, out uint punValue);
        new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
        new void GetDouble([In] ref Guid guidKey, out double pfValue);
        new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
        new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
        new void GetString([In] ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
        new void GetAllocatedString([In] ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
        new void GetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
        new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
        new void SetItem([In] ref Guid guidKey, IntPtr Value);
        new void DeleteItem([In] ref Guid guidKey);
        new void DeleteAllItems();
        new void SetUINT32([In] ref Guid guidKey, uint unValue);
        new void SetUINT64([In] ref Guid guidKey, ulong unValue);
        new void SetDouble([In] ref Guid guidKey, double fValue);
        new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
        new void SetString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new void SetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
        new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new void LockStore();
        new void UnlockStore();
        new void GetCount(out uint pcItems);
        new void GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new void CopyAllItems(IMFAttributes pDest);
        void GetSampleFlags(out uint pdwSampleFlags);
        void SetSampleFlags(uint dwSampleFlags);
        void GetSampleTime(out long phnsSampleTime);
        void SetSampleTime(long hnsSampleTime);
        void GetSampleDuration(out long phnsSampleDuration);
        void SetSampleDuration(long hnsSampleDuration);
        void GetBufferCount(out uint pdwBufferCount);
        void GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        void ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        void AddBuffer(IMFMediaBuffer pBuffer);
        void RemoveBufferByIndex(uint dwIndex);
        void RemoveAllBuffers();
        void GetTotalLength(out uint pcbTotalLength);
        void CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport, Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFActivate : IMFAttributes
    {
        new void GetItem([In] ref Guid guidKey, IntPtr pValue);
        new void GetItemType([In] ref Guid guidKey, out int pType);
        new void CompareItem([In] ref Guid guidKey, IntPtr Value, out bool pbResult);
        new void Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        new void GetUINT32([In] ref Guid guidKey, out uint punValue);
        new void GetUINT64([In] ref Guid guidKey, out ulong punValue);
        new void GetDouble([In] ref Guid guidKey, out double pfValue);
        new void GetGUID([In] ref Guid guidKey, out Guid pguidValue);
        new void GetStringLength([In] ref Guid guidKey, out uint pcchLength);
        new void GetString([In] ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
        new void GetAllocatedString([In] ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        new void GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
        new void GetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
        new void GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        new void GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, out IntPtr ppv);
        new void SetItem([In] ref Guid guidKey, IntPtr Value);
        new void DeleteItem([In] ref Guid guidKey);
        new void DeleteAllItems();
        new void SetUINT32([In] ref Guid guidKey, uint unValue);
        new void SetUINT64([In] ref Guid guidKey, ulong unValue);
        new void SetDouble([In] ref Guid guidKey, double fValue);
        new void SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
        new void SetString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new void SetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
        new void SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new void LockStore();
        new void UnlockStore();
        new void GetCount(out uint pcItems);
        new void GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new void CopyAllItems(IMFAttributes pDest);
        void ActivateObject([In] ref Guid riid, out IntPtr ppv);
        void ShutdownObject();
        void DetachObject();
    }

    [ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFTransform
    {
        void GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);
        void GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);
        void GetStreamIDs(uint dwInputIDArraySize, IntPtr pdwInputIDs, uint dwOutputIDArraySize, IntPtr pdwOutputIDs);
        void GetInputStreamInfo(uint dwInputStreamID, out ulong pStreamInfo);
        void GetOutputStreamInfo(uint dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
        void GetAttributes(out IMFAttributes pAttributes);
        void GetInputStreamAttributes(uint dwInputStreamID, out IMFAttributes pAttributes);
        void GetOutputStreamAttributes(uint dwOutputStreamID, out IMFAttributes pAttributes);
        void DeleteInputStream(uint dwStreamID);
        void AddInputStreams(uint cStreams, IntPtr adwStreamIDs);
        [PreserveSig] int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int SetInputType(uint dwInputStreamID, IMFMediaType pType, uint dwFlags);
        [PreserveSig] int SetOutputType(uint dwOutputStreamID, IMFMediaType pType, uint dwFlags);
        void GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);
        void GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);
        void GetInputStatus(uint dwInputStreamID, out uint pdwFlags);
        void GetOutputStatus(out uint pdwFlags);
        void SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        void ProcessEvent(uint dwInputStreamID, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(int eMessage, IntPtr ulParam);
        [PreserveSig] int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);
        [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount, ref MFT_OUTPUT_DATA_BUFFER pOutputSamples, out uint pdwStatus);
    }

    [ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICodecAPI
    {
        [PreserveSig] int IsSupported([In] ref Guid Api);
        [PreserveSig] int IsModifiable([In] ref Guid Api);
        [PreserveSig] int GetParameterRange([In] ref Guid Api, out PROPVARIANT ValueMin, out PROPVARIANT ValueMax, out PROPVARIANT SteppingDelta);
        [PreserveSig] int GetParameterValues([In] ref Guid Api, out IntPtr Values, out uint ValuesCount);
        [PreserveSig] int GetDefaultValue([In] ref Guid Api, out PROPVARIANT Value);
        [PreserveSig] int GetValue([In] ref Guid Api, out PROPVARIANT Value);
        [PreserveSig] int SetValue([In] ref Guid Api, [In] ref PROPVARIANT Value);
    }
}


public enum VideoCodec
{
    H264,
    Hevc
}

public static class VideoCodecs
{
    public const string H264Wire = "h264";
    public const string HevcWire = "hevc";
    public const byte H264PacketType = 0x01;
    public const byte HevcPacketType = 0x03;

    public static string ToWire(VideoCodec codec) => codec == VideoCodec.Hevc ? HevcWire : H264Wire;

    public static VideoCodec FromWire(string? value) =>
        string.Equals(value, HevcWire, StringComparison.OrdinalIgnoreCase) ? VideoCodec.Hevc : VideoCodec.H264;

    public static byte ToPacketType(VideoCodec codec) => codec == VideoCodec.Hevc ? HevcPacketType : H264PacketType;

    public static VideoCodec? FromPacketType(byte type) => type switch
    {
        H264PacketType => VideoCodec.H264,
        HevcPacketType => VideoCodec.Hevc,
        _ => null
    };

    public static string DisplayName(VideoCodec codec) => codec == VideoCodec.Hevc ? "H.265" : "H.264";

    internal static Guid Subtype(VideoCodec codec) => codec == VideoCodec.Hevc ? MfNative.MFVideoFormat_HEVC : MfNative.MFVideoFormat_H264;

    /// <summary>Annex B 码流里是否带参数集（H.264 SPS，H.265 VPS/SPS）。</summary>
    public static bool ContainsParameterSets(VideoCodec codec, ReadOnlySpan<byte> annexB)
    {
        for (int i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0 || annexB[i + 2] != 1) continue;
            byte header = annexB[i + 3];
            bool isParamSet = codec == VideoCodec.H264
                ? (header & 0x1F) == 7
                : ((header >> 1) & 0x3F) is 32 or 33;
            if (isParamSet) return true;
            i += 2;
        }
        return false;
    }

    internal static bool StartsWithStartCode(ReadOnlySpan<byte> data) =>
        (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1) ||
        (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1);
}

/// <summary>
/// Media Foundation 视频编码器。硬件编码器（NVENC / Quick Sync / AMF）都是异步 MFT，
/// 由事件线程驱动；软件编码器是同步 MFT。两种情况的输出都通过回调送出。
/// </summary>
public sealed class MfVideoEncoder : IDisposable
{
    private readonly MfNative.IMFActivate _activate;
    private readonly MfNative.IMFTransform _mft;
    private readonly MfNative.ICodecAPI? _codecApi;
    private readonly bool _async;
    private readonly IntPtr _eventGenerator;
    private readonly Thread? _eventThread;
    private readonly Action<byte[], long> _onOutput;
    private readonly object _outputLock = new();
    private readonly byte[] _nv12Buffer;
    private readonly int _faultThreshold;
    private bool _mftProvidesSamples;
    private MfNative.IMFSample? _reusableOutSample;
    private byte[]? _sequenceHeader;
    private int _needInput;
    private int _callsWithoutOutput;
    private int _consecutiveErrors;
    private DateTime _lastKeyFrameRequest = DateTime.MinValue;
    private volatile bool _disposed;

    public VideoCodec Codec { get; }
    public bool IsHardware { get; }
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>持续出错或长时间没有输出；调用方应换用下一个编码器。</summary>
    public bool IsFaulted { get; private set; }

    public byte[] Nv12Buffer => _nv12Buffer;

    public string Description => $"{VideoCodecs.DisplayName(Codec)} {(IsHardware ? "硬件" : "软件")}编码";

    /// <summary>创建指定编码格式的编码器；hardware=true 只找硬件 MFT，false 只找软件 MFT。找不到或都配置失败时返回 null。</summary>
    public static MfVideoEncoder? TryCreate(VideoCodec codec, bool hardware, int width, int height, int fps, int bitrateMbps, Action<byte[], long> onOutput)
    {
        MfNative.EnsureInitialized();
        uint flags = hardware
            ? MfNative.MFT_ENUM_FLAG_HARDWARE | MfNative.MFT_ENUM_FLAG_SORTANDFILTER
            : MfNative.MFT_ENUM_FLAG_SYNCMFT | MfNative.MFT_ENUM_FLAG_LOCALMFT | MfNative.MFT_ENUM_FLAG_SORTANDFILTER;
        return MfNative.EnumerateAndActivate(
            MfNative.MFT_CATEGORY_VIDEO_ENCODER, flags, MfNative.MFVideoFormat_NV12, VideoCodecs.Subtype(codec),
            (activate, mft) => new MfVideoEncoder(activate, mft, codec, hardware, width, height, fps, bitrateMbps, onOutput));
    }

    private MfVideoEncoder(
        MfNative.IMFActivate activate,
        MfNative.IMFTransform mft,
        VideoCodec codec,
        bool hardware,
        int width,
        int height,
        int fps,
        int bitrateMbps,
        Action<byte[], long> onOutput)
    {
        _activate = activate;
        _mft = mft;
        _onOutput = onOutput;
        Codec = codec;
        IsHardware = hardware;
        Width = width & ~1;
        Height = height & ~1;
        _nv12Buffer = new byte[Width * Height * 3 / 2];
        _faultThreshold = Math.Max(30, fps * 3);
        Name = MfNative.GetString(activate, MfNative.MFT_FRIENDLY_NAME_Attribute) ?? VideoCodecs.DisplayName(codec);

        MfNative.IMFAttributes? attrs = null;
        try { _mft.GetAttributes(out attrs); } catch { }
        if (attrs is not null)
        {
            if (MfNative.GetUInt32(attrs, MfNative.MF_TRANSFORM_ASYNC, 0) != 0)
            {
                var unlock = MfNative.MF_TRANSFORM_ASYNC_UNLOCK;
                attrs.SetUINT32(ref unlock, 1);
                _async = true;
            }
            MfNative.TrySetUInt32(attrs, MfNative.MF_LOW_LATENCY, 1);
        }

        _codecApi = _mft as MfNative.ICodecAPI;
        if (_codecApi is not null)
        {
            var vtTrue = new MfNative.PROPVARIANT { vt = 11, val = -1 }; // VT_BOOL VARIANT_TRUE
            var zeroU32 = new MfNative.PROPVARIANT { vt = 19, val = 0 };  // VT_UI4 = 0
            var lowLat = MfNative.CODECAPI_AVLowLatencyMode;
            var bCount = MfNative.CODECAPI_AVEncMPVDefaultBPictureCount;
            _codecApi.SetValue(ref lowLat, ref vtTrue);
            _codecApi.SetValue(ref bCount, ref zeroU32);
        }

        MfNative.MFCreateMediaType(out var outType);
        SetGuid(outType, MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Video);
        SetGuid(outType, MfNative.MF_MT_SUBTYPE, VideoCodecs.Subtype(codec));
        SetU32(outType, MfNative.MF_MT_AVG_BITRATE, (uint)(bitrateMbps * 1_000_000));
        SetU32(outType, MfNative.MF_MT_INTERLACE_MODE, 2); // Progressive
        SetU64(outType, MfNative.MF_MT_FRAME_SIZE, (ulong)MfNative.Pack2UInt32((uint)Width, (uint)Height));
        SetU64(outType, MfNative.MF_MT_FRAME_RATE, (ulong)MfNative.Pack2UInt32((uint)fps, 1));
        SetU64(outType, MfNative.MF_MT_PIXEL_ASPECT_RATIO, (ulong)MfNative.Pack2UInt32(1, 1));
        if (codec == VideoCodec.Hevc)
        {
            SetU32(outType, MfNative.MF_MT_MPEG2_PROFILE, 1); // eAVEncH265VProfile_Main_420_8
        }
        Marshal.ThrowExceptionForHR(_mft.SetOutputType(0, outType, 0));

        MfNative.MFCreateMediaType(out var inType);
        SetGuid(inType, MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Video);
        SetGuid(inType, MfNative.MF_MT_SUBTYPE, MfNative.MFVideoFormat_NV12);
        SetU32(inType, MfNative.MF_MT_INTERLACE_MODE, 2);
        SetU64(inType, MfNative.MF_MT_FRAME_SIZE, (ulong)MfNative.Pack2UInt32((uint)Width, (uint)Height));
        SetU64(inType, MfNative.MF_MT_FRAME_RATE, (ulong)MfNative.Pack2UInt32((uint)fps, 1));
        SetU64(inType, MfNative.MF_MT_PIXEL_ASPECT_RATIO, (ulong)MfNative.Pack2UInt32(1, 1));
        Marshal.ThrowExceptionForHR(_mft.SetInputType(0, inType, 0));

        AllocateOutputSample();

        if (_async)
        {
            IntPtr unk = Marshal.GetIUnknownForObject(_mft);
            try
            {
                Marshal.ThrowExceptionForHR(MfNative.QueryInterface(unk, MfNative.IID_IMFMediaEventGenerator, out _eventGenerator));
            }
            finally
            {
                Marshal.Release(unk);
            }
        }

        Marshal.ThrowExceptionForHR(_mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero));
        Marshal.ThrowExceptionForHR(_mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero));

        if (_async)
        {
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "MF encoder events" };
            _eventThread.Start();
        }
    }

    private void AllocateOutputSample()
    {
        _mft.GetOutputStreamInfo(0, out var streamInfo);
        _mftProvidesSamples = (streamInfo.dwFlags & MfNative.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        if (_mftProvidesSamples)
        {
            _reusableOutSample = null;
            return;
        }
        MfNative.MFCreateSample(out var sample);
        uint bufSize = Math.Max(streamInfo.cbSize, (uint)(Width * Height));
        MfNative.MFCreateMemoryBuffer(bufSize, out var buf);
        sample.AddBuffer(buf);
        _reusableOutSample = sample;
    }

    public void RequestKeyFrame()
    {
        if (_disposed || _codecApi is null) return;
        var now = DateTime.UtcNow;
        if ((now - _lastKeyFrameRequest).TotalMilliseconds < 200) return;
        _lastKeyFrameRequest = now;

        var forceKey = MfNative.CODECAPI_AVEncVideoForceKeyFrame;
        var val = new MfNative.PROPVARIANT { vt = 19, val = 1 };
        try { _codecApi.SetValue(ref forceKey, ref val); } catch { }
    }

    /// <summary>编码 <see cref="Nv12Buffer"/> 里的当前画面。输出经构造时的回调送出（异步编码器在事件线程上回调）。</summary>
    public void EncodeNv12(long ptsHns, long durationHns)
    {
        if (_disposed || IsFaulted) return;

        if (Interlocked.Increment(ref _callsWithoutOutput) > _faultThreshold)
        {
            IsFaulted = true;
            return;
        }

        if (_async)
        {
            // 编码器还没要输入：丢掉这一帧，不阻塞采集
            if (Interlocked.Decrement(ref _needInput) < 0)
            {
                Interlocked.Increment(ref _needInput);
                return;
            }
        }

        MfNative.MFCreateSample(out var inSample);
        MfNative.MFCreateMemoryBuffer((uint)_nv12Buffer.Length, out var inBuf);
        int hrIn;
        try
        {
            inBuf.Lock(out IntPtr pDst, out _, out _);
            Marshal.Copy(_nv12Buffer, 0, pDst, _nv12Buffer.Length);
            inBuf.Unlock();
            inBuf.SetCurrentLength((uint)_nv12Buffer.Length);
            inSample.AddBuffer(inBuf);
            inSample.SetSampleTime(ptsHns);
            inSample.SetSampleDuration(durationHns);
            hrIn = _mft.ProcessInput(0, inSample, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(inBuf);
            Marshal.ReleaseComObject(inSample);
        }

        if (hrIn < 0 && hrIn != MfNative.MF_E_NOTACCEPTING)
        {
            if (++_consecutiveErrors > 10) IsFaulted = true;
            return;
        }
        _consecutiveErrors = 0;

        if (!_async)
        {
            lock (_outputLock)
            {
                for (int i = 0; i < 8; i++)
                {
                    int hr = PullOutput();
                    if (hr == MfNative.MF_E_TRANSFORM_STREAM_CHANGE) continue;
                    if (hr < 0) break;
                }
            }
        }
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            if (MfNative.GetEvent(_eventGenerator, 0, out uint type, out _) < 0) break;
            if (type == MfNative.METransformNeedInput)
            {
                Interlocked.Increment(ref _needInput);
            }
            else if (type == MfNative.METransformHaveOutput)
            {
                lock (_outputLock)
                {
                    if (_disposed) break;
                    if (PullOutput() == MfNative.MF_E_TRANSFORM_STREAM_CHANGE)
                    {
                        PullOutput();
                    }
                }
            }
        }
    }

    private int PullOutput()
    {
        var outBufData = new MfNative.MFT_OUTPUT_DATA_BUFFER
        {
            dwStreamID = 0,
            pSample = _mftProvidesSamples || _reusableOutSample is null ? IntPtr.Zero : Marshal.GetIUnknownForObject(_reusableOutSample)
        };

        try
        {
            int hr = _mft.ProcessOutput(0, 1, ref outBufData, out _);
            if (hr == MfNative.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (_mft.GetOutputAvailableType(0, 0, out var newType) >= 0)
                {
                    _mft.SetOutputType(0, newType, 0);
                }
                AllocateOutputSample();
                _sequenceHeader = null;
                return hr;
            }
            if (hr < 0) return hr;
            if (outBufData.pSample == IntPtr.Zero) return MfNative.MF_E_TRANSFORM_NEED_MORE_INPUT;

            var bytes = MfNative.CopySampleBytes(outBufData.pSample);
            if (bytes.Length == 0) return 0;

            long pts = MfNative.SampleTime(outBufData.pSample);
            bool keyFrame = MfNative.AttrGetUInt32(outBufData.pSample, MfNative.MFSampleExtension_CleanPoint, 0) != 0;
            Interlocked.Exchange(ref _callsWithoutOutput, 0);

            // 个别硬件编码器只把参数集放在媒体类型里：补到关键帧前面，观看端才能从这一帧开始解码
            if (keyFrame && !VideoCodecs.ContainsParameterSets(Codec, bytes))
            {
                _sequenceHeader ??= ReadSequenceHeader();
                if (_sequenceHeader is { Length: > 0 } header)
                {
                    var merged = new byte[header.Length + bytes.Length];
                    Buffer.BlockCopy(header, 0, merged, 0, header.Length);
                    Buffer.BlockCopy(bytes, 0, merged, header.Length, bytes.Length);
                    bytes = merged;
                }
            }

            try { _onOutput(bytes, pts); } catch { }
            return 0;
        }
        finally
        {
            if (outBufData.pSample != IntPtr.Zero) Marshal.Release(outBufData.pSample);
            if (outBufData.pEvents != IntPtr.Zero) Marshal.Release(outBufData.pEvents);
        }
    }

    private byte[] ReadSequenceHeader()
    {
        try
        {
            _mft.GetOutputCurrentType(0, out var current);
            var blob = MfNative.GetBlob(current, MfNative.MF_MT_MPEG_SEQUENCE_HEADER);
            return blob is not null && VideoCodecs.StartsWithStartCode(blob) ? blob : [];
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero); } catch { }

        bool eventThreadExited = true;
        if (_async)
        {
            try { _mft.ProcessMessage(MfNative.MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero); } catch { }
            // 关闭 MFT 让阻塞中的 GetEvent 返回
            IntPtr unk = Marshal.GetIUnknownForObject(_mft);
            try
            {
                if (MfNative.QueryInterface(unk, MfNative.IID_IMFShutdown, out var shutdown) >= 0)
                {
                    MfNative.Shutdown(shutdown);
                    Marshal.Release(shutdown);
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.Release(unk);
            }
            eventThreadExited = _eventThread?.Join(1000) ?? true;
        }

        lock (_outputLock)
        {
            try { _activate.ShutdownObject(); } catch { }
            if (_eventGenerator != IntPtr.Zero && eventThreadExited) Marshal.Release(_eventGenerator);
            Marshal.ReleaseComObject(_mft);
        }
    }

    internal static void SetGuid(MfNative.IMFMediaType mt, Guid k, Guid v) => mt.SetGUID(ref k, ref v);
    internal static void SetU32(MfNative.IMFMediaType mt, Guid k, uint v) => mt.SetUINT32(ref k, v);
    internal static void SetU64(MfNative.IMFMediaType mt, Guid k, ulong v) => mt.SetUINT64(ref k, v);
    internal static Guid GetGuid(MfNative.IMFMediaType mt, Guid k) { mt.GetGUID(ref k, out var v); return v; }
    internal static ulong GetU64(MfNative.IMFMediaType mt, Guid k) { mt.GetUINT64(ref k, out var v); return v; }
}

/// <summary>解码后的 NV12 画面接收方：显存纹理（硬件解码）或内存（软件解码）。</summary>
public interface IVideoFrameSink
{
    void PresentNv12Texture(IntPtr texture, uint subresource, int width, int height);

    void PresentNv12Memory(IntPtr data, int stride, int planeHeight, int width, int height);
}

/// <summary>
/// Media Foundation 视频解码器。传入 D3D11 设备时启用 DXVA 硬件解码，输出直接留在显存；
/// 否则（或显卡不支持该格式时）由解码器软解，输出在内存。
/// </summary>
public sealed class MfVideoDecoder : IDisposable
{
    private readonly MfNative.IMFActivate? _activate;
    private readonly MfNative.IMFTransform _mft;
    private readonly IntPtr _deviceManager;
    private bool _mftProvidesSamples;
    private MfNative.IMFSample? _reusableOutSample;
    private int _frameW;
    private int _frameH;
    private bool _disposed;

    public VideoCodec Codec { get; }

    /// <summary>解码器接受了 D3D11 设备（DXVA 可用时输出显存纹理）。</summary>
    public bool UsesD3D11 { get; }

    /// <summary>已经实际输出过显存纹理，即确实在用硬件解码。</summary>
    public bool IsHardwareOutput { get; private set; }

    public int RequestedWidth { get; }
    public int RequestedHeight { get; }
    public int DisplayWidth { get; private set; }
    public int DisplayHeight { get; private set; }

    public static MfVideoDecoder Create(VideoCodec codec, int width, int height, IntPtr d3dDevice, bool requireD3D11)
    {
        MfNative.EnsureInitialized();
        if (codec == VideoCodec.Hevc)
        {
            var viaEnum = MfNative.EnumerateAndActivate(
                MfNative.MFT_CATEGORY_VIDEO_DECODER,
                MfNative.MFT_ENUM_FLAG_SYNCMFT | MfNative.MFT_ENUM_FLAG_LOCALMFT | MfNative.MFT_ENUM_FLAG_SORTANDFILTER,
                MfNative.MFVideoFormat_HEVC, MfNative.MFVideoFormat_NV12,
                (activate, mft) => new MfVideoDecoder(activate, mft, codec, width, height, d3dDevice, requireD3D11));
            if (viaEnum is not null) return viaEnum;
        }

        var clsid = codec == VideoCodec.Hevc ? MfNative.CLSID_MSH265DecoderMFT : MfNative.CLSID_CMSH264DecoderMFT;
        var iid = typeof(MfNative.IMFTransform).GUID;
        int hr = MfNative.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out IntPtr pMft);
        if (hr < 0)
        {
            throw new NotSupportedException($"系统没有可用的 {VideoCodecs.DisplayName(codec)} 解码器。");
        }
        var transform = (MfNative.IMFTransform)Marshal.GetObjectForIUnknown(pMft);
        Marshal.Release(pMft);
        try
        {
            return new MfVideoDecoder(null, transform, codec, width, height, d3dDevice, requireD3D11);
        }
        catch
        {
            Marshal.ReleaseComObject(transform);
            throw;
        }
    }

    /// <summary>能否用这块 D3D11 设备创建该格式的硬件解码器。</summary>
    public static bool ProbeD3D11(VideoCodec codec, IntPtr d3dDevice)
    {
        if (d3dDevice == IntPtr.Zero) return false;
        try
        {
            using var probe = Create(codec, 1920, 1080, d3dDevice, requireD3D11: true);
            return probe.UsesD3D11;
        }
        catch
        {
            return false;
        }
    }

    private MfVideoDecoder(
        MfNative.IMFActivate? activate,
        MfNative.IMFTransform mft,
        VideoCodec codec,
        int width,
        int height,
        IntPtr d3dDevice,
        bool requireD3D11)
    {
        _activate = activate;
        _mft = mft;
        Codec = codec;
        RequestedWidth = width;
        RequestedHeight = height;
        DisplayWidth = width;
        DisplayHeight = height;
        _frameW = width;
        _frameH = height;

        try
        {
            MfNative.IMFAttributes? attrs = null;
            try { _mft.GetAttributes(out attrs); } catch { }
            if (attrs is not null)
            {
                MfNative.TrySetUInt32(attrs, MfNative.MF_LOW_LATENCY, 1);
            }

            if (_mft is MfNative.ICodecAPI codecApi)
            {
                var vtTrue = new MfNative.PROPVARIANT { vt = 11, val = -1 };
                var lowLat = MfNative.CODECAPI_AVLowLatencyMode;
                try { codecApi.SetValue(ref lowLat, ref vtTrue); } catch { }
            }

            if (d3dDevice != IntPtr.Zero && attrs is not null && MfNative.GetUInt32(attrs, MfNative.MF_SA_D3D11_AWARE, 0) != 0 &&
                MfNative.MFCreateDXGIDeviceManager(out uint token, out IntPtr manager) >= 0)
            {
                if (MfNative.DeviceManagerReset(manager, d3dDevice, token) >= 0 &&
                    _mft.ProcessMessage(MfNative.MFT_MESSAGE_SET_D3D_MANAGER, manager) >= 0)
                {
                    _deviceManager = manager;
                    UsesD3D11 = true;
                }
                else
                {
                    Marshal.Release(manager);
                }
            }

            if (requireD3D11 && !UsesD3D11)
            {
                throw new NotSupportedException("解码器不支持 D3D11 硬件加速。");
            }

            MfNative.MFCreateMediaType(out var inType);
            MfVideoEncoder.SetGuid(inType, MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Video);
            MfVideoEncoder.SetGuid(inType, MfNative.MF_MT_SUBTYPE, VideoCodecs.Subtype(codec));
            MfVideoEncoder.SetU32(inType, MfNative.MF_MT_INTERLACE_MODE, 2);
            MfVideoEncoder.SetU64(inType, MfNative.MF_MT_FRAME_SIZE, (ulong)MfNative.Pack2UInt32((uint)width, (uint)height));
            Marshal.ThrowExceptionForHR(_mft.SetInputType(0, inType, 0));

            if (!SelectNv12Output())
            {
                throw new NotSupportedException("解码器不支持 NV12 输出。");
            }

            _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        }
        catch
        {
            if (_deviceManager != IntPtr.Zero)
            {
                try { _mft.ProcessMessage(MfNative.MFT_MESSAGE_SET_D3D_MANAGER, IntPtr.Zero); } catch { }
                Marshal.Release(_deviceManager);
                _deviceManager = IntPtr.Zero;
            }
            throw;
        }
    }

    private bool SelectNv12Output()
    {
        for (uint i = 0; ; i++)
        {
            if (_mft.GetOutputAvailableType(0, i, out var avail) < 0) return false;
            if (MfVideoEncoder.GetGuid(avail, MfNative.MF_MT_SUBTYPE) != MfNative.MFVideoFormat_NV12) continue;
            if (_mft.SetOutputType(0, avail, 0) < 0) return false;

            try
            {
                ulong fs = MfVideoEncoder.GetU64(avail, MfNative.MF_MT_FRAME_SIZE);
                _frameW = (int)(fs >> 32);
                _frameH = (int)(fs & 0xFFFFFFFF);
            }
            catch
            {
            }

            // 编码尺寸按 16 对齐（1080 → 1088），真正的画面区域在 display aperture 里
            var aperture = MfNative.GetBlob(avail, MfNative.MF_MT_MINIMUM_DISPLAY_APERTURE);
            int apW = 0, apH = 0;
            if (aperture is { Length: >= 16 })
            {
                apW = BitConverter.ToInt32(aperture, 8);
                apH = BitConverter.ToInt32(aperture, 12);
            }
            DisplayWidth = apW > 0 && apW <= _frameW ? apW : Math.Min(RequestedWidth, _frameW);
            DisplayHeight = apH > 0 && apH <= _frameH ? apH : Math.Min(RequestedHeight, _frameH);
            DisplayWidth &= ~1;
            DisplayHeight &= ~1;

            AllocateOutputSample();
            return true;
        }
    }

    private void AllocateOutputSample()
    {
        _mft.GetOutputStreamInfo(0, out var info);
        // D3D11 模式下解码器用自己的显存样本池，能自己提供样本时一律让它提供
        _mftProvidesSamples = (info.dwFlags & (MfNative.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MfNative.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        if (_mftProvidesSamples)
        {
            _reusableOutSample = null;
            return;
        }
        MfNative.MFCreateSample(out var sample);
        uint sz = Math.Max(info.cbSize, (uint)(_frameW * _frameH * 3 / 2));
        MfNative.MFCreateMemoryBuffer(sz, out var buf);
        sample.AddBuffer(buf);
        _reusableOutSample = sample;
    }

    /// <summary>送入一帧码流，把解出的画面交给 sink，返回本次呈现的帧数。</summary>
    public int Decode(byte[] annexB, long ptsHns, IVideoFrameSink sink)
    {
        if (_disposed) return 0;

        MfNative.MFCreateSample(out var inSample);
        MfNative.MFCreateMemoryBuffer((uint)annexB.Length, out var inBuf);
        int hrIn;
        try
        {
            inBuf.Lock(out IntPtr pDst, out _, out _);
            Marshal.Copy(annexB, 0, pDst, annexB.Length);
            inBuf.Unlock();
            inBuf.SetCurrentLength((uint)annexB.Length);
            inSample.AddBuffer(inBuf);
            inSample.SetSampleTime(ptsHns);

            hrIn = _mft.ProcessInput(0, inSample, 0);
            int presented = 0;
            if (hrIn == MfNative.MF_E_NOTACCEPTING)
            {
                // 解码器积压了输出：先取走再重送
                presented += DrainOutputs(sink);
                hrIn = _mft.ProcessInput(0, inSample, 0);
            }
            if (hrIn < 0) return presented;
            return presented + DrainOutputs(sink);
        }
        finally
        {
            Marshal.ReleaseComObject(inBuf);
            Marshal.ReleaseComObject(inSample);
        }
    }

    private int DrainOutputs(IVideoFrameSink sink)
    {
        int presented = 0;
        for (int i = 0; i < 8; i++)
        {
            var outBufData = new MfNative.MFT_OUTPUT_DATA_BUFFER
            {
                dwStreamID = 0,
                pSample = _mftProvidesSamples || _reusableOutSample is null ? IntPtr.Zero : Marshal.GetIUnknownForObject(_reusableOutSample)
            };

            try
            {
                int hr = _mft.ProcessOutput(0, 1, ref outBufData, out _);
                if (hr == MfNative.MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    if (!SelectNv12Output()) break;
                    continue;
                }
                if (hr < 0 || outBufData.pSample == IntPtr.Zero) break;
                if (PresentSample(outBufData.pSample, sink)) presented++;
            }
            finally
            {
                if (outBufData.pSample != IntPtr.Zero) Marshal.Release(outBufData.pSample);
                if (outBufData.pEvents != IntPtr.Zero) Marshal.Release(outBufData.pEvents);
            }
        }
        return presented;
    }

    private bool PresentSample(IntPtr sample, IVideoFrameSink sink)
    {
        if (UsesD3D11)
        {
            IntPtr buffer = MfNative.SampleBuffer(sample);
            if (buffer == IntPtr.Zero) return false;
            try
            {
                if (MfNative.QueryInterface(buffer, MfNative.IID_IMFDXGIBuffer, out var dxgiBuffer) >= 0)
                {
                    try
                    {
                        if (MfNative.DxgiBufferGetTexture(dxgiBuffer, out var texture, out uint subresource) < 0) return false;
                        try
                        {
                            sink.PresentNv12Texture(texture, subresource, DisplayWidth, DisplayHeight);
                            IsHardwareOutput = true;
                            return true;
                        }
                        finally
                        {
                            Marshal.Release(texture);
                        }
                    }
                    finally
                    {
                        Marshal.Release(dxgiBuffer);
                    }
                }
            }
            finally
            {
                Marshal.Release(buffer);
            }
        }

        // 软解输出：连续内存，行距等于帧宽，UV 平面紧跟 Y 平面
        IntPtr contiguous = MfNative.SampleContiguousBuffer(sample);
        if (contiguous == IntPtr.Zero) return false;
        try
        {
            if (MfNative.BufferLock(contiguous, out var data, out uint length) < 0) return false;
            try
            {
                if (length < (uint)(_frameW * _frameH * 3 / 2)) return false;
                sink.PresentNv12Memory(data, _frameW, _frameH, DisplayWidth, DisplayHeight);
                return true;
            }
            finally
            {
                MfNative.BufferUnlock(contiguous);
            }
        }
        finally
        {
            Marshal.Release(contiguous);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero); } catch { }
        try { _activate?.ShutdownObject(); } catch { }
        Marshal.ReleaseComObject(_mft);
        if (_deviceManager != IntPtr.Zero) Marshal.Release(_deviceManager);
    }
}
