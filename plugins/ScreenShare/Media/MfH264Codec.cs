using System.Runtime.InteropServices;

namespace ScreenShare.Media;

internal static class MfNative
{
    public const int MF_VERSION = 0x00020070;
    public const uint MFSTARTUP_NOSOCKET = 1;
    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    public const uint MFT_ENUM_FLAG_ALL = 0x0000003F;

    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");
    public static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

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

public sealed class MfH264Encoder : IDisposable
{
    private readonly MfNative.IMFTransform _mft;
    private readonly MfNative.ICodecAPI? _codecApi;
    private readonly bool _mftProvidesSamples;
    private readonly MfNative.IMFSample? _reusableOutSample;
    private readonly byte[] _nv12Buffer;
    private DateTime _lastKeyFrameRequest = DateTime.MinValue;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    public MfH264Encoder(int width, int height, int fps, int bitrateMbps)
    {
        MfNative.EnsureInitialized();
        // Ensure even dimensions for NV12 / H.264
        Width = width & ~1;
        Height = height & ~1;
        _nv12Buffer = new byte[Width * Height * 3 / 2];

        var inInfo = new MfNative.MFT_REGISTER_TYPE_INFO { guidMajorType = MfNative.MFMediaType_Video, guidSubtype = MfNative.MFVideoFormat_NV12 };
        var outInfo = new MfNative.MFT_REGISTER_TYPE_INFO { guidMajorType = MfNative.MFMediaType_Video, guidSubtype = MfNative.MFVideoFormat_H264 };
        var cat = MfNative.MFT_CATEGORY_VIDEO_ENCODER;

        int hr = MfNative.MFTEnumEx(ref cat, MfNative.MFT_ENUM_FLAG_HARDWARE | MfNative.MFT_ENUM_FLAG_SYNCMFT | MfNative.MFT_ENUM_FLAG_SORTANDFILTER,
            ref inInfo, ref outInfo, out IntPtr pActivates, out uint count);
        if (hr < 0 || count == 0)
        {
            hr = MfNative.MFTEnumEx(ref cat, MfNative.MFT_ENUM_FLAG_ALL, ref inInfo, ref outInfo, out pActivates, out count);
        }
        if (hr < 0 || count == 0)
        {
            throw new InvalidOperationException("系统未找到可用的 H.264 Media Foundation 编码器。");
        }

        try
        {
            IntPtr firstActivatePtr = Marshal.ReadIntPtr(pActivates, 0);
            var activate = (MfNative.IMFActivate)Marshal.GetObjectForIUnknown(firstActivatePtr);
            var mftGuid = typeof(MfNative.IMFTransform).GUID;
            activate.ActivateObject(ref mftGuid, out IntPtr pMft);
            _mft = (MfNative.IMFTransform)Marshal.GetObjectForIUnknown(pMft);
            Marshal.Release(pMft);
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
        SetGuid(outType, MfNative.MF_MT_SUBTYPE, MfNative.MFVideoFormat_H264);
        SetU32(outType, MfNative.MF_MT_AVG_BITRATE, (uint)(bitrateMbps * 1_000_000));
        SetU32(outType, MfNative.MF_MT_INTERLACE_MODE, 2); // Progressive
        SetU64(outType, MfNative.MF_MT_FRAME_SIZE, (ulong)MfNative.Pack2UInt32((uint)Width, (uint)Height));
        SetU64(outType, MfNative.MF_MT_FRAME_RATE, (ulong)MfNative.Pack2UInt32((uint)fps, 1));
        SetU64(outType, MfNative.MF_MT_PIXEL_ASPECT_RATIO, (ulong)MfNative.Pack2UInt32(1, 1));
        Marshal.ThrowExceptionForHR(_mft.SetOutputType(0, outType, 0));

        MfNative.MFCreateMediaType(out var inType);
        SetGuid(inType, MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Video);
        SetGuid(inType, MfNative.MF_MT_SUBTYPE, MfNative.MFVideoFormat_NV12);
        SetU32(inType, MfNative.MF_MT_INTERLACE_MODE, 2);
        SetU64(inType, MfNative.MF_MT_FRAME_SIZE, (ulong)MfNative.Pack2UInt32((uint)Width, (uint)Height));
        SetU64(inType, MfNative.MF_MT_FRAME_RATE, (ulong)MfNative.Pack2UInt32((uint)fps, 1));
        SetU64(inType, MfNative.MF_MT_PIXEL_ASPECT_RATIO, (ulong)MfNative.Pack2UInt32(1, 1));
        Marshal.ThrowExceptionForHR(_mft.SetInputType(0, inType, 0));

        _mft.GetOutputStreamInfo(0, out var streamInfo);
        _mftProvidesSamples = (streamInfo.dwFlags & MfNative.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        if (!_mftProvidesSamples)
        {
            MfNative.MFCreateSample(out _reusableOutSample);
            uint bufSize = Math.Max(streamInfo.cbSize, (uint)(Width * Height));
            MfNative.MFCreateMemoryBuffer(bufSize, out var buf);
            _reusableOutSample.AddBuffer(buf);
        }

        _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
    }

    public byte[] Nv12Buffer => _nv12Buffer;

    public void RequestKeyFrame()
    {
        if (_disposed || _codecApi is null) return;
        var now = DateTime.UtcNow;
        if ((now - _lastKeyFrameRequest).TotalMilliseconds < 200) return;
        _lastKeyFrameRequest = now;

        var forceKey = MfNative.CODECAPI_AVEncVideoForceKeyFrame;
        var val = new MfNative.PROPVARIANT { vt = 19, val = 1 };
        _codecApi.SetValue(ref forceKey, ref val);
    }

    public byte[]? EncodeNv12(long ptsHns, long durationHns)
    {
        if (_disposed) return null;

        MfNative.MFCreateSample(out var inSample);
        MfNative.MFCreateMemoryBuffer((uint)_nv12Buffer.Length, out var inBuf);
        inBuf.Lock(out IntPtr pDst, out _, out _);
        Marshal.Copy(_nv12Buffer, 0, pDst, _nv12Buffer.Length);
        inBuf.Unlock();
        inBuf.SetCurrentLength((uint)_nv12Buffer.Length);
        inSample.AddBuffer(inBuf);
        inSample.SetSampleTime(ptsHns);
        inSample.SetSampleDuration(durationHns);

        int hrIn = _mft.ProcessInput(0, inSample, 0);
        if (hrIn < 0)
        {
            return null;
        }

        var outBufData = new MfNative.MFT_OUTPUT_DATA_BUFFER
        {
            dwStreamID = 0,
            pSample = _mftProvidesSamples ? IntPtr.Zero : Marshal.GetIUnknownForObject(_reusableOutSample!)
        };

        try
        {
            int hrOut = _mft.ProcessOutput(0, 1, ref outBufData, out _);
            if (hrOut == MfNative.MF_E_TRANSFORM_NEED_MORE_INPUT || hrOut < 0)
            {
                return null;
            }

            if (outBufData.pSample == IntPtr.Zero)
            {
                return null;
            }

            var outSample = (MfNative.IMFSample)Marshal.GetObjectForIUnknown(outBufData.pSample);
            outSample.ConvertToContiguousBuffer(out var mediaBuf);
            mediaBuf.Lock(out IntPtr pOutData, out _, out uint len);
            var result = new byte[len];
            Marshal.Copy(pOutData, result, 0, (int)len);
            mediaBuf.Unlock();
            return result;
        }
        finally
        {
            if (outBufData.pSample != IntPtr.Zero) Marshal.Release(outBufData.pSample);
            if (outBufData.pEvents != IntPtr.Zero) Marshal.Release(outBufData.pEvents);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero); } catch { }
        Marshal.ReleaseComObject(_mft);
    }

    internal static void SetGuid(MfNative.IMFMediaType mt, Guid k, Guid v) => mt.SetGUID(ref k, ref v);
    internal static void SetU32(MfNative.IMFMediaType mt, Guid k, uint v) => mt.SetUINT32(ref k, v);
    internal static void SetU64(MfNative.IMFMediaType mt, Guid k, ulong v) => mt.SetUINT64(ref k, v);
    internal static Guid GetGuid(MfNative.IMFMediaType mt, Guid k) { mt.GetGUID(ref k, out var v); return v; }
    internal static ulong GetU64(MfNative.IMFMediaType mt, Guid k) { mt.GetUINT64(ref k, out var v); return v; }
}

public sealed class MfH264Decoder : IDisposable
{
    private readonly MfNative.IMFTransform _mft;
    private bool _mftProvidesSamples;
    private MfNative.IMFSample? _reusableOutSample;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public MfH264Decoder(int width, int height)
    {
        MfNative.EnsureInitialized();
        Width = width;
        Height = height;

        var clsid = MfNative.CLSID_CMSH264DecoderMFT;
        var iid = typeof(MfNative.IMFTransform).GUID;
        Marshal.ThrowExceptionForHR(MfNative.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out IntPtr pMft));
        _mft = (MfNative.IMFTransform)Marshal.GetObjectForIUnknown(pMft);
        Marshal.Release(pMft);

        if (_mft is MfNative.ICodecAPI codecApi)
        {
            var vtTrue = new MfNative.PROPVARIANT { vt = 11, val = -1 };
            var lowLat = MfNative.CODECAPI_AVLowLatencyMode;
            codecApi.SetValue(ref lowLat, ref vtTrue);
        }

        ConfigureTypes(width, height);
        _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
    }

    private void ConfigureTypes(int width, int height)
    {
        MfNative.MFCreateMediaType(out var inType);
        MfH264Encoder.SetGuid(inType, MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Video);
        MfH264Encoder.SetGuid(inType, MfNative.MF_MT_SUBTYPE, MfNative.MFVideoFormat_H264);
        MfH264Encoder.SetU64(inType, MfNative.MF_MT_FRAME_SIZE, (ulong)MfNative.Pack2UInt32((uint)width, (uint)height));
        Marshal.ThrowExceptionForHR(_mft.SetInputType(0, inType, 0));

        for (uint i = 0; ; i++)
        {
            int hr = _mft.GetOutputAvailableType(0, i, out var avail);
            if (hr < 0) break;
            var sub = MfH264Encoder.GetGuid(avail, MfNative.MF_MT_SUBTYPE);
            if (sub == MfNative.MFVideoFormat_NV12)
            {
                Marshal.ThrowExceptionForHR(_mft.SetOutputType(0, avail, 0));
                break;
            }
        }

        AllocateOutputSampleIfNeeded();
    }

    private void AllocateOutputSampleIfNeeded()
    {
        _mft.GetOutputStreamInfo(0, out var info);
        _mftProvidesSamples = (info.dwFlags & MfNative.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        if (!_mftProvidesSamples)
        {
            MfNative.MFCreateSample(out _reusableOutSample);
            uint sz = Math.Max(info.cbSize, (uint)(Width * Height * 3 / 2));
            MfNative.MFCreateMemoryBuffer(sz, out var buf);
            _reusableOutSample.AddBuffer(buf);
        }
        else
        {
            _reusableOutSample = null;
        }
    }

    public byte[]? Decode(byte[] h264AnnexB, long ptsHns)
    {
        if (_disposed) return null;

        MfNative.MFCreateSample(out var inSample);
        MfNative.MFCreateMemoryBuffer((uint)h264AnnexB.Length, out var inBuf);
        inBuf.Lock(out IntPtr pDst, out _, out _);
        Marshal.Copy(h264AnnexB, 0, pDst, h264AnnexB.Length);
        inBuf.Unlock();
        inBuf.SetCurrentLength((uint)h264AnnexB.Length);
        inSample.AddBuffer(inBuf);
        inSample.SetSampleTime(ptsHns);

        if (_mft.ProcessInput(0, inSample, 0) < 0)
        {
            return null;
        }

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
                for (uint i = 0; ; i++)
                {
                    if (_mft.GetOutputAvailableType(0, i, out var avail) < 0) break;
                    var sub = MfH264Encoder.GetGuid(avail, MfNative.MF_MT_SUBTYPE);
                    if (sub == MfNative.MFVideoFormat_NV12)
                    {
                        _mft.SetOutputType(0, avail, 0);
                        ulong fs = MfH264Encoder.GetU64(avail, MfNative.MF_MT_FRAME_SIZE);
                        Width = (int)(fs >> 32);
                        Height = (int)(fs & 0xFFFFFFFF);
                        break;
                    }
                }
                AllocateOutputSampleIfNeeded();
                return null;
            }

            if (hr < 0 || outBufData.pSample == IntPtr.Zero)
            {
                return null;
            }

            var outSample = (MfNative.IMFSample)Marshal.GetObjectForIUnknown(outBufData.pSample);
            outSample.ConvertToContiguousBuffer(out var mediaBuf);
            mediaBuf.Lock(out IntPtr pData, out _, out uint len);
            var nv12 = new byte[len];
            Marshal.Copy(pData, nv12, 0, (int)len);
            mediaBuf.Unlock();
            return nv12;
        }
        finally
        {
            if (outBufData.pSample != IntPtr.Zero) Marshal.Release(outBufData.pSample);
            if (outBufData.pEvents != IntPtr.Zero) Marshal.Release(outBufData.pEvents);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(MfNative.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero); } catch { }
        Marshal.ReleaseComObject(_mft);
    }

    internal static void SetGuid(MfNative.IMFMediaType mt, Guid k, Guid v) => mt.SetGUID(ref k, ref v);
    internal static void SetU32(MfNative.IMFMediaType mt, Guid k, uint v) => mt.SetUINT32(ref k, v);
    internal static void SetU64(MfNative.IMFMediaType mt, Guid k, ulong v) => mt.SetUINT64(ref k, v);
    internal static Guid GetGuid(MfNative.IMFMediaType mt, Guid k) { mt.GetGUID(ref k, out var v); return v; }
    internal static ulong GetU64(MfNative.IMFMediaType mt, Guid k) { mt.GetUINT64(ref k, out var v); return v; }
}
