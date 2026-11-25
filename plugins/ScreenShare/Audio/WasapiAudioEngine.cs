using System.Runtime.InteropServices;
using Concentus.Enums;
using Concentus.Structs;

namespace ScreenShare.Audio;

internal static class WasapiNative
{
    public const int CLSCTX_ALL = 23;
    public const int eRender = 0;
    public const int eConsole = 0;
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        [In] ref Guid rclsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int ShareMode, uint StreamFlags, long hnsBufferDuration, long hnsPeriodicity, [In] ref WAVEFORMATEX pFormat, IntPtr AudioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int ShareMode, [In] ref WAVEFORMATEX pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint NumFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint NumFramesRequested, out IntPtr ppData);
        [PreserveSig] int ReleaseBuffer(uint NumFramesWritten, uint dwFlags);
    }

    public static WAVEFORMATEX CreatePcm48kStereo16() => new()
    {
        wFormatTag = 1, // PCM
        nChannels = 2,
        nSamplesPerSec = 48000,
        wBitsPerSample = 16,
        nBlockAlign = 4,
        nAvgBytesPerSec = 48000 * 4,
        cbSize = 0
    };
}

public sealed class WasapiLoopbackOpusCapturer : IDisposable
{
    private const int FrameSamplesPerChannel = 960; // 20ms @ 48kHz
    private readonly OpusEncoder _encoder = new(48000, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
    private readonly Action<byte[]> _onOpusPacket;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public WasapiLoopbackOpusCapturer(Action<byte[]> onOpusPacket)
    {
        _onOpusPacket = onOpusPacket;
        _encoder.Bitrate = 96000;
    }

    public void Start()
    {
        _loopTask ??= Task.Run(CaptureLoopAsync);
    }

    private async Task CaptureLoopAsync()
    {
        WasapiNative.IAudioClient? audioClient = null;
        WasapiNative.IAudioCaptureClient? captureClient = null;
        try
        {
            var clsid = WasapiNative.CLSID_MMDeviceEnumerator;
            var iidEnum = typeof(WasapiNative.IMMDeviceEnumerator).GUID;
            Marshal.ThrowExceptionForHR(WasapiNative.CoCreateInstance(ref clsid, IntPtr.Zero, WasapiNative.CLSCTX_ALL, ref iidEnum, out var enumObj));
            var enumerator = (WasapiNative.IMMDeviceEnumerator)enumObj;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(WasapiNative.eRender, WasapiNative.eConsole, out var device));

            var iidClient = WasapiNative.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(device.Activate(ref iidClient, WasapiNative.CLSCTX_ALL, IntPtr.Zero, out var clientObj));
            audioClient = (WasapiNative.IAudioClient)clientObj;

            var fmt = WasapiNative.CreatePcm48kStereo16();
            uint flags = WasapiNative.AUDCLNT_STREAMFLAGS_LOOPBACK |
                         WasapiNative.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                         WasapiNative.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Marshal.ThrowExceptionForHR(audioClient.Initialize(WasapiNative.AUDCLNT_SHAREMODE_SHARED, flags, 400_000, 0, ref fmt, IntPtr.Zero));

            var iidCap = WasapiNative.IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref iidCap, out var capObj));
            captureClient = (WasapiNative.IAudioCaptureClient)capObj;
            Marshal.ThrowExceptionForHR(audioClient.Start());

            var ring = new short[FrameSamplesPerChannel * 2];
            int ringCount = 0;
            var opusOut = new byte[512];

            while (!_cts.IsCancellationRequested)
            {
                while (captureClient.GetNextPacketSize(out uint packetFrames) == 0 && packetFrames > 0)
                {
                    if (captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint bufFlags, out _, out _) != 0)
                        break;

                    try
                    {
                        int sampleCount = (int)numFrames * 2;
                        var temp = new short[sampleCount];
                        if ((bufFlags & 0x2) == 0) // not silent
                        {
                            Marshal.Copy(pData, temp, 0, sampleCount);
                        }

                        int offset = 0;
                        while (offset < sampleCount)
                        {
                            int toCopy = Math.Min(ring.Length - ringCount, sampleCount - offset);
                            Array.Copy(temp, offset, ring, ringCount, toCopy);
                            ringCount += toCopy;
                            offset += toCopy;

                            if (ringCount == ring.Length)
                            {
                                int encoded = _encoder.Encode(ring, 0, FrameSamplesPerChannel, opusOut, 0, opusOut.Length);
                                if (encoded > 0)
                                {
                                    var pkt = new byte[encoded];
                                    Buffer.BlockCopy(opusOut, 0, pkt, 0, encoded);
                                    _onOpusPacket(pkt);
                                }
                                ringCount = 0;
                            }
                        }
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(numFrames);
                    }
                }

                await Task.Delay(5, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            try { audioClient?.Stop(); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask?.Wait(500); } catch { }
        _cts.Dispose();
    }
}

public sealed class WasapiOpusPlayer : IDisposable
{
    private const int FrameSamplesPerChannel = 960;
    private readonly OpusDecoder _decoder = new(48000, 2);
    private WasapiNative.IAudioClient? _audioClient;
    private WasapiNative.IAudioRenderClient? _renderClient;
    private uint _bufferFrames;
    private readonly short[] _pcmBuffer = new short[FrameSamplesPerChannel * 2];
    private readonly object _lock = new();
    private bool _disposed;

    public bool Muted { get; set; }

    public WasapiOpusPlayer()
    {
        try
        {
            var clsid = WasapiNative.CLSID_MMDeviceEnumerator;
            var iidEnum = typeof(WasapiNative.IMMDeviceEnumerator).GUID;
            Marshal.ThrowExceptionForHR(WasapiNative.CoCreateInstance(ref clsid, IntPtr.Zero, WasapiNative.CLSCTX_ALL, ref iidEnum, out var enumObj));
            var enumerator = (WasapiNative.IMMDeviceEnumerator)enumObj;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(WasapiNative.eRender, WasapiNative.eConsole, out var device));

            var iidClient = WasapiNative.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(device.Activate(ref iidClient, WasapiNative.CLSCTX_ALL, IntPtr.Zero, out var clientObj));
            _audioClient = (WasapiNative.IAudioClient)clientObj;

            var fmt = WasapiNative.CreatePcm48kStereo16();
            uint flags = WasapiNative.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | WasapiNative.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Marshal.ThrowExceptionForHR(_audioClient.Initialize(WasapiNative.AUDCLNT_SHAREMODE_SHARED, flags, 600_000, 0, ref fmt, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(_audioClient.GetBufferSize(out _bufferFrames));

            var iidRender = WasapiNative.IID_IAudioRenderClient;
            Marshal.ThrowExceptionForHR(_audioClient.GetService(ref iidRender, out var renderObj));
            _renderClient = (WasapiNative.IAudioRenderClient)renderObj;
            Marshal.ThrowExceptionForHR(_audioClient.Start());
        }
        catch
        {
            _audioClient = null;
            _renderClient = null;
        }
    }

    public void PlayOpusPacket(byte[] opus)
    {
        if (_disposed || Muted || _audioClient is null || _renderClient is null) return;
        lock (_lock)
        {
            int decodedFrames = _decoder.Decode(opus, 0, opus.Length, _pcmBuffer, 0, FrameSamplesPerChannel, false);
            if (decodedFrames <= 0) return;

            if (_audioClient.GetCurrentPadding(out uint padding) != 0) return;
            uint available = _bufferFrames - padding;
            if (available < (uint)decodedFrames) return;

            if (_renderClient.GetBuffer((uint)decodedFrames, out IntPtr pData) == 0)
            {
                Marshal.Copy(_pcmBuffer, 0, pData, decodedFrames * 2);
                _renderClient.ReleaseBuffer((uint)decodedFrames, 0);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _audioClient?.Stop(); } catch { }
    }
}
