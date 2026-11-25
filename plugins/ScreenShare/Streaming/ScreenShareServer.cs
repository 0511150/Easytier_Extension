using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenShare.Audio;
using ScreenShare.Capture;
using ScreenShare.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenShare.Streaming;

public static class ResolutionPresets
{
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("original", "原始分辨率"),
        // 16:9 常见显示器 / 笔记本分辨率
        ("3840x2160", "[16:9] 3840x2160 (4K / 2160p)"),
        ("2560x1440", "[16:9] 2560x1440 (2K / 1440p)"),
        ("1920x1080", "[16:9] 1920x1080 (FHD / 1080p)"),
        ("1600x900", "[16:9] 1600x900 (HD+ / 900p)"),
        ("1366x768", "[16:9] 1366x768 (WXGA)"),
        ("1280x720", "[16:9] 1280x720 (HD / 720p)"),
        // 16:10 常见笔记本 / 专业显示器分辨率
        ("3840x2400", "[16:10] 3840x2400 (4K+ / WQUXGA)"),
        ("3200x2000", "[16:10] 3200x2000 (3.2K)"),
        ("2880x1800", "[16:10] 2880x1800 (2.8K)"),
        ("2560x1600", "[16:10] 2560x1600 (2.5K / WQXGA)"),
        ("2240x1400", "[16:10] 2240x1400 (2.2K)"),
        ("1920x1200", "[16:10] 1920x1200 (FHD+ / WUXGA)"),
        ("1680x1050", "[16:10] 1680x1050 (WSXGA+)"),
        ("1440x900", "[16:10] 1440x900 (WXGA+)"),
        ("1280x800", "[16:10] 1280x800 (WXGA)")
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "original";
        var v = value.Trim();
        if (string.Equals(v, "1080p", StringComparison.OrdinalIgnoreCase)) return "1920x1080";
        if (string.Equals(v, "720p", StringComparison.OrdinalIgnoreCase)) return "1280x720";
        foreach (var (key, _) in All)
        {
            if (string.Equals(key, v, StringComparison.OrdinalIgnoreCase)) return key;
        }
        return "original";
    }

    public static (int w, int h) ResolveTargetSize(int srcW, int srcH, string resolution, bool isWindowCapture)
    {
        srcW = Math.Max(64, srcW & ~1);
        srcH = Math.Max(64, srcH & ~1);

        var norm = Normalize(resolution);
        if (isWindowCapture || norm == "original")
        {
            const int maxW = 3840;
            const int maxH = 2400;
            if (srcW > maxW || srcH > maxH)
            {
                double scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
                int w = Math.Max(64, (int)Math.Round(srcW * scale) & ~1);
                int h = Math.Max(64, (int)Math.Round(srcH * scale) & ~1);
                return (w, h);
            }
            return (srcW, srcH);
        }

        var parts = norm.Split('x');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int presetW) &&
            int.TryParse(parts[1], out int presetH) &&
            presetW >= 64 && presetH >= 64)
        {
            return (presetW & ~1, presetH & ~1);
        }

        return (srcW, srcH);
    }
}

public sealed class ShareSettings
{
    [JsonPropertyName("capture_source")]
    public string CaptureSource { get; set; } = "fullscreen"; // "fullscreen" | "window"

    [JsonPropertyName("capture_api")]
    public string CaptureApi { get; set; } = "wgc"; // "wgc" | "dxgi"

    [JsonPropertyName("last_window_title")]
    public string LastWindowTitle { get; set; } = string.Empty;

    [JsonIgnore]
    public IntPtr TargetWindowHwnd { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "original";

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 60;

    [JsonPropertyName("bitrate_mbps")]
    public int BitrateMbps { get; set; } = 8;

    [JsonPropertyName("share_audio")]
    public bool ShareAudio { get; set; } = true;

    [JsonPropertyName("allow_control")]
    public bool AllowRemoteControl { get; set; }

    [JsonPropertyName("target_network")]
    public string TargetNetworkId { get; set; } = "__all__";
}

public sealed class ScreenShareServer : IAsyncDisposable
{
    public const int ControlPort = 39600;
    public const int MediaUdpPort = 39601;
    private const int MaxSlicePayload = 1160;

    private readonly ShareSettings _settings;
    private readonly ConcurrentDictionary<string, TcpListener> _listeners = new();
    private readonly ConcurrentDictionary<string, ViewerSession> _viewers = new();
    private readonly UdpClient _udpSender = new();
    private readonly object _controlLock = new();

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private WasapiLoopbackOpusCapturer? _audioCapturer;
    private MfH264Encoder? _encoder;
    private string? _controllerSessionId;
    private uint _frameCounter;
    private uint _audioSeq;
    private int _activeSrcW = 1920;
    private int _activeSrcH = 1080;
    private int _activeDstW = 1920;
    private int _activeDstH = 1080;

    public int ActiveViewerCount => _viewers.Count;
    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public event Action? StateChanged;
    public event Action? CaptureTargetClosed;

    public ScreenShareServer(ShareSettings settings)
    {
        _settings = settings;
    }

    public void Start(IEnumerable<string> bindVirtualIps)
    {
        Stop();
        _cts = new CancellationTokenSource();

        foreach (var ipStr in bindVirtualIps.Distinct(StringComparer.Ordinal))
        {
            if (!IPAddress.TryParse(ipStr, out var ip)) continue;
            try
            {
                var listener = new TcpListener(ip, ControlPort);
                listener.Start();
                if (_listeners.TryAdd(ipStr, listener))
                {
                    _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                }
            }
            catch
            {
            }
        }

        _captureTask = Task.Run(() => CaptureAndFanoutLoopAsync(_cts.Token));
        if (_settings.ShareAudio)
        {
            _audioCapturer = new WasapiLoopbackOpusCapturer(OnOpusAudioPacket);
            _audioCapturer.Start();
        }
        StateChanged?.Invoke();
    }

    public async Task UpdateControlPolicyAsync()
    {
        string? revokedSessionId = null;
        lock (_controlLock)
        {
            if (!_settings.AllowRemoteControl)
            {
                revokedSessionId = _controllerSessionId;
                _controllerSessionId = null;
            }
        }

        var policyMsg = new ControlMsg
        {
            Type = "control_policy",
            AllowControl = _settings.AllowRemoteControl
        };

        foreach (var session in _viewers.Values)
        {
            try
            {
                await session.SendAsync(policyMsg);
                if (revokedSessionId is not null && session.Id == revokedSessionId)
                {
                    await session.SendAsync(new ControlMsg { Type = "control_result", Granted = false });
                }
            }
            catch
            {
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _audioCapturer?.Dispose();
        _audioCapturer = null;

        foreach (var l in _listeners.Values)
        {
            try { l.Stop(); } catch { }
        }
        _listeners.Clear();

        foreach (var v in _viewers.Values)
        {
            v.Dispose();
        }
        _viewers.Clear();
        _controllerSessionId = null;
        _cts?.Dispose();
        _cts = null;
        StateChanged?.Invoke();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleViewerAsync(tcp, ct));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleViewerAsync(TcpClient tcp, CancellationToken ct)
    {
        var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            var helloLine = await reader.ReadLineAsync(ct);
            if (helloLine is null) return;
            var hello = JsonSerializer.Deserialize<ControlMsg>(helloLine);
            if (hello is null || hello.Type != "hello") return;

            int viewerUdpPort = hello.UdpPort > 0 ? hello.UdpPort : MediaUdpPort;
            var session = new ViewerSession(sessionId, tcp, writer, new IPEndPoint(remoteIp, viewerUdpPort));
            _viewers[sessionId] = session;
            StateChanged?.Invoke();

            var enc = _encoder;
            await session.SendAsync(new ControlMsg
            {
                Type = "welcome",
                SessionId = sessionId,
                Width = enc?.Width ?? 1920,
                Height = enc?.Height ?? 1080,
                Fps = _settings.Fps,
                AllowControl = _settings.AllowRemoteControl,
                ControllerBusy = _controllerSessionId is not null
            });
            enc?.RequestKeyFrame();

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var msg = JsonSerializer.Deserialize<ControlMsg>(line);
                if (msg is null) continue;

                switch (msg.Type)
                {
                    case "pli":
                        _encoder?.RequestKeyFrame();
                        break;
                    case "mute":
                        session.AudioMuted = msg.Muted;
                        break;
                    case "acquire_control":
                    {
                        bool granted = false;
                        lock (_controlLock)
                        {
                            if (_settings.AllowRemoteControl && (_controllerSessionId is null || _controllerSessionId == sessionId))
                            {
                                _controllerSessionId = sessionId;
                                granted = true;
                            }
                        }
                        await session.SendAsync(new ControlMsg { Type = "control_result", Granted = granted });
                        break;
                    }
                    case "release_control":
                        lock (_controlLock)
                        {
                            if (_controllerSessionId == sessionId) _controllerSessionId = null;
                        }
                        break;
                    case "mouse":
                        if (_settings.AllowRemoteControl && _controllerSessionId == sessionId)
                        {
                            InjectMouse(msg.X, msg.Y, msg.Flags, msg.MouseData);
                        }
                        break;
                    case "key":
                        if (_settings.AllowRemoteControl && _controllerSessionId == sessionId)
                        {
                            InjectKey(msg.ScanCode, msg.Extended, msg.KeyUp);
                        }
                        break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            _viewers.TryRemove(sessionId, out _);
            lock (_controlLock)
            {
                if (_controllerSessionId == sessionId) _controllerSessionId = null;
            }
            tcp.Dispose();
            StateChanged?.Invoke();
        }
    }

    private async Task CaptureAndFanoutLoopAsync(CancellationToken ct)
    {
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_0],
            out ID3D11Device? device).CheckError();
        var context = device!.ImmediateContext;

        IScreenCapturer? capturer = null;
        ID3D11Texture2D? staging = null;
        bool notifyTargetClosed = false;
        bool isWindowMode = string.Equals(_settings.CaptureSource, "window", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (isWindowMode)
            {
                try
                {
                    capturer = new WgcScreenCapturer(device, _settings.TargetWindowHwnd);
                }
                catch
                {
                    notifyTargetClosed = true;
                    return;
                }
            }
            else
            {
                try
                {
                    capturer = _settings.CaptureApi == "dxgi"
                        ? new DxgiScreenCapturer(device)
                        : new WgcScreenCapturer(device);
                }
                catch
                {
                    capturer = new DxgiScreenCapturer(device);
                }
            }

            int srcW = Math.Max(2, capturer.Width);
            int srcH = Math.Max(2, capturer.Height);
            var (dstW, dstH) = ResolutionPresets.ResolveTargetSize(srcW, srcH, _settings.Resolution, isWindowMode);

            _encoder = CreateEncoderWithFallback(dstW, dstH, _settings.Fps, _settings.BitrateMbps);
            dstW = _encoder.Width;
            dstH = _encoder.Height;
            _activeSrcW = srcW;
            _activeSrcH = srcH;
            _activeDstW = dstW;
            _activeDstH = dstH;
            ColorConvert.FillNv12Black(_encoder.Nv12Buffer, dstW, dstH);

            staging = CreateStagingTexture(device, srcW, srcH);

            int frameIntervalMs = Math.Max(10, 1000 / Math.Max(1, _settings.Fps));
            long frameDurationHns = 10_000_000L / Math.Max(1, _settings.Fps);
            long ptsHns = 0;

            while (!ct.IsCancellationRequested)
            {
                if (capturer.IsTargetClosed)
                {
                    notifyTargetClosed = true;
                    break;
                }

                var startTick = Environment.TickCount64;
                if (_viewers.IsEmpty)
                {
                    await Task.Delay(50, ct);
                    continue;
                }

                if (capturer.TryAcquireFrame(out var gpuTex) && gpuTex is not null)
                {
                    int curSrcW = Math.Max(2, capturer.Width);
                    int curSrcH = Math.Max(2, capturer.Height);
                    if (curSrcW != srcW || curSrcH != srcH)
                    {
                        srcW = curSrcW;
                        srcH = curSrcH;
                        staging.Dispose();
                        staging = CreateStagingTexture(device, srcW, srcH);

                        var (nextDstW, nextDstH) = ResolutionPresets.ResolveTargetSize(srcW, srcH, _settings.Resolution, isWindowMode);
                        if (nextDstW != dstW || nextDstH != dstH)
                        {
                            _encoder.Dispose();
                            _encoder = CreateEncoderWithFallback(nextDstW, nextDstH, _settings.Fps, _settings.BitrateMbps);
                            dstW = _encoder.Width;
                            dstH = _encoder.Height;
                            BroadcastVideoConfig(dstW, dstH, _settings.Fps);
                        }

                        _activeSrcW = srcW;
                        _activeSrcH = srcH;
                        _activeDstW = dstW;
                        _activeDstH = dstH;
                        ColorConvert.FillNv12Black(_encoder.Nv12Buffer, dstW, dstH);
                    }

                    context.CopyResource(staging, gpuTex);
                    ConvertStagingToNv12(context, staging, srcW, srcH, _encoder.Nv12Buffer, dstW, dstH);
                    var encoded = _encoder.EncodeNv12(ptsHns, frameDurationHns);
                    ptsHns += frameDurationHns;

                    if (encoded is { Length: > 0 })
                    {
                        FanoutVideoFrame(encoded, ptsHns / 10);
                    }
                }

                int elapsed = (int)(Environment.TickCount64 - startTick);
                int sleep = frameIntervalMs - elapsed;
                if (sleep > 1)
                {
                    await Task.Delay(sleep, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (isWindowMode) notifyTargetClosed = true;
        }
        finally
        {
            _encoder?.Dispose();
            _encoder = null;
            staging?.Dispose();
            capturer?.Dispose();
            context.Dispose();
            device.Dispose();

            if (notifyTargetClosed && !ct.IsCancellationRequested)
            {
                _ = Task.Run(() =>
                {
                    Stop();
                    CaptureTargetClosed?.Invoke();
                });
            }
        }
    }

    private static ID3D11Texture2D CreateStagingTexture(ID3D11Device device, int width, int height) =>
        device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read
        });

    private static MfH264Encoder CreateEncoderWithFallback(int dstW, int dstH, int fps, int bitrateMbps)
    {
        try
        {
            return new MfH264Encoder(dstW, dstH, fps, bitrateMbps);
        }
        catch when (dstW > 1920 || dstH > 1200)
        {
            double scale = Math.Min(1920.0 / dstW, 1200.0 / dstH);
            int fbW = Math.Max(64, (int)Math.Round(dstW * scale) & ~1);
            int fbH = Math.Max(64, (int)Math.Round(dstH * scale) & ~1);
            return new MfH264Encoder(fbW, fbH, fps, bitrateMbps);
        }
    }

    private void BroadcastVideoConfig(int width, int height, int fps)
    {
        var msg = new ControlMsg
        {
            Type = "video_config",
            Width = width,
            Height = height,
            Fps = fps
        };
        foreach (var session in _viewers.Values)
        {
            _ = session.SendAsync(msg);
        }
    }

    private static unsafe void ConvertStagingToNv12(
        ID3D11DeviceContext context,
        ID3D11Texture2D staging,
        int srcW,
        int srcH,
        byte[] nv12Out,
        int dstW,
        int dstH)
    {
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            ColorConvert.BgraToNv12((byte*)mapped.DataPointer, (int)mapped.RowPitch, srcW, srcH, nv12Out, dstW, dstH);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private void FanoutVideoFrame(byte[] annexB, long ptsUs)
    {
        uint frameId = Interlocked.Increment(ref _frameCounter);
        int sliceCount = (annexB.Length + MaxSlicePayload - 1) / MaxSlicePayload;
        if (sliceCount <= 0) return;

        var packet = new byte[19 + MaxSlicePayload];
        packet[0] = 0x45; // 'E'
        packet[1] = 0x54; // 'T'
        packet[2] = 0x01; // Video

        for (int i = 0; i < sliceCount; i++)
        {
            int offset = i * MaxSlicePayload;
            int len = Math.Min(MaxSlicePayload, annexB.Length - offset);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3, 4), frameId);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7, 2), (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9, 2), (ushort)sliceCount);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(11, 8), ptsUs);
            Buffer.BlockCopy(annexB, offset, packet, 19, len);

            int totalLen = 19 + len;
            foreach (var v in _viewers.Values)
            {
                try { _udpSender.Send(packet, totalLen, v.UdpEndPoint); } catch { }
            }
        }
    }

    private void OnOpusAudioPacket(byte[] opus)
    {
        if (!_settings.ShareAudio || _viewers.IsEmpty) return;
        uint seq = Interlocked.Increment(ref _audioSeq);
        var packet = new byte[15 + opus.Length];
        packet[0] = 0x45;
        packet[1] = 0x54;
        packet[2] = 0x02; // Audio
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3, 4), seq);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(7, 8), Environment.TickCount64 * 1000);
        Buffer.BlockCopy(opus, 0, packet, 15, opus.Length);

        foreach (var v in _viewers.Values)
        {
            if (v.AudioMuted) continue;
            try { _udpSender.Send(packet, packet.Length, v.UdpEndPoint); } catch { }
        }
    }

    private void InjectMouse(double normX, double normY, uint flags, int mouseData)
    {
        var (padX, padY, fitW, fitH) = ColorConvert.ComputeLetterboxFit(_activeSrcW, _activeSrcH, _activeDstW, _activeDstH);
        double dstPxX = Math.Clamp(normX, 0, 1) * _activeDstW;
        double dstPxY = Math.Clamp(normY, 0, 1) * _activeDstH;
        double srcNormX = Math.Clamp((dstPxX - padX) / Math.Max(1, fitW), 0.0, 1.0);
        double srcNormY = Math.Clamp((dstPxY - padY) / Math.Max(1, fitH), 0.0, 1.0);

        int absX;
        int absY;
        uint extraFlags = 0x8000; // MOUSEEVENTF_ABSOLUTE

        if (string.Equals(_settings.CaptureSource, "window", StringComparison.OrdinalIgnoreCase) &&
            WindowEnumerator.TryGetWindowCaptureBounds(_settings.TargetWindowHwnd, out int winLeft, out int winTop, out int winW, out int winH))
        {
            int vLeft = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
            int vTop = GetSystemMetrics(77);  // SM_YVIRTUALSCREEN
            int vWidth = Math.Max(1, GetSystemMetrics(78));  // SM_CXVIRTUALSCREEN
            int vHeight = Math.Max(1, GetSystemMetrics(79)); // SM_CYVIRTUALSCREEN

            double screenX = winLeft + srcNormX * winW;
            double screenY = winTop + srcNormY * winH;
            absX = (int)Math.Clamp((screenX - vLeft) / vWidth * 65535.0, 0, 65535);
            absY = (int)Math.Clamp((screenY - vTop) / vHeight * 65535.0, 0, 65535);
            extraFlags |= 0x4000; // MOUSEEVENTF_VIRTUALDESK
        }
        else
        {
            absX = (int)(srcNormX * 65535.0);
            absY = (int)(srcNormY * 65535.0);
        }

        var input = new INPUT
        {
            type = 0, // INPUT_MOUSE
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    mouseData = (uint)mouseData,
                    dwFlags = flags | extraFlags
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void InjectKey(ushort scanCode, bool extended, bool keyUp)
    {
        uint flags = 0x0008; // KEYEVENTF_SCANCODE
        if (extended) flags |= 0x0001;
        if (keyUp) flags |= 0x0002;
        var input = new INPUT
        {
            type = 1, // INPUT_KEYBOARD
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wScan = scanCode,
                    dwFlags = flags
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _udpSender.Dispose();
        if (_captureTask is not null)
        {
            try { await _captureTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    private sealed class ViewerSession(string id, TcpClient tcp, StreamWriter writer, IPEndPoint udpEndPoint) : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        public string Id { get; } = id;
        public IPEndPoint UdpEndPoint { get; } = udpEndPoint;
        public bool AudioMuted { get; set; }

        public async Task SendAsync(ControlMsg msg)
        {
            await _lock.WaitAsync();
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(msg));
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Dispose()
        {
            tcp.Dispose();
            _lock.Dispose();
        }
    }

    internal sealed class ControlMsg
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("viewer_name")] public string? ViewerName { get; set; }
        [JsonPropertyName("udp_port")] public int UdpPort { get; set; }
        [JsonPropertyName("session_id")] public string? SessionId { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("fps")] public int Fps { get; set; }
        [JsonPropertyName("allow_control")] public bool AllowControl { get; set; }
        [JsonPropertyName("controller_busy")] public bool ControllerBusy { get; set; }
        [JsonPropertyName("granted")] public bool Granted { get; set; }
        [JsonPropertyName("muted")] public bool Muted { get; set; }
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("flags")] public uint Flags { get; set; }
        [JsonPropertyName("mouse_data")] public int MouseData { get; set; }
        [JsonPropertyName("scan_code")] public ushort ScanCode { get; set; }
        [JsonPropertyName("extended")] public bool Extended { get; set; }
        [JsonPropertyName("key_up")] public bool KeyUp { get; set; }
    }
}
