using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ScreenShare.Audio;
using ScreenShare.Media;
using ScreenShare.Viewer;

namespace ScreenShare.Streaming;

public sealed class ScreenShareClient : IAsyncDisposable
{
    private readonly EmbeddedD3D11Renderer _renderer;
    private readonly WasapiOpusPlayer _audioPlayer = new();
    private readonly SemaphoreSlim _tcpWriteLock = new(1, 1);

    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private UdpClient? _udp;
    // DXVA 解码配置：H.264 VLD（无胶片颗粒）与 HEVC Main
    private static readonly Guid D3D11DecoderProfileH264 = new("1B81BE68-A0C7-11D3-B984-00C04F2E73C5");
    private static readonly Guid D3D11DecoderProfileHevcMain = new("5B11D51B-2F4C-4452-BCC3-09F2A1160CC0");
    private const int MaxFramesWithoutOutput = 90;

    private readonly object _decoderLock = new();
    private MfVideoDecoder? _decoder;
    private bool _h264HardwareFailed;
    private bool? _hevcHardwareSupported;
    private int _framesWithoutOutput;
    private CancellationTokenSource? _cts;
    private string _displayLabel = string.Empty;
    private int _streamWidth = 1920;
    private int _streamHeight = 1080;
    private int _streamFps;
    private string _decodeLabel = string.Empty;

    public bool IsConnected => _tcp is not null && _tcp.Connected;
    public bool IsMuted { get; private set; }
    public bool IsControlling { get; private set; }
    public bool RemoteAllowsControl { get; private set; }
    public string RemoteLabel { get; private set; } = "未连接任何共享节点";

    public event Action? StateChanged;
    public event Action? Disconnected;

    public ScreenShareClient(EmbeddedD3D11Renderer renderer)
    {
        _renderer = renderer;
        _renderer.MouseEvent += (x, y, flags, data) => _ = SendControlMsgAsync(new ScreenShareServer.ControlMsg
        {
            Type = "mouse",
            X = x,
            Y = y,
            Flags = flags,
            MouseData = data
        });
        _renderer.KeyEvent += (scan, ext, up) => _ = SendControlMsgAsync(new ScreenShareServer.ControlMsg
        {
            Type = "key",
            ScanCode = scan,
            Extended = ext,
            KeyUp = up
        });
        _renderer.ControlEscapePressed += () => _ = ToggleControlAsync(false);
    }

    public async Task ConnectAsync(string targetVirtualIp, int port, string? localBindIp, string displayLabel, bool autoRequestControl = false)
    {
        await DisconnectAsync();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _udp = IPAddress.TryParse(localBindIp, out var bindIp)
            ? new UdpClient(new IPEndPoint(bindIp, 0))
            : new UdpClient(0);
        int localUdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(targetVirtualIp, port, ct);
        var stream = _tcp.GetStream();
        var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await SendControlMsgAsync(new ScreenShareServer.ControlMsg
        {
            Type = "hello",
            ViewerName = Environment.MachineName,
            UdpPort = localUdpPort,
            Codecs = SupportedCodecs()
        });

        var welcomeLine = await reader.ReadLineAsync(ct);
        if (welcomeLine is null)
        {
            throw new IOException("远端未响应握手。");
        }

        var welcome = JsonSerializer.Deserialize<ScreenShareServer.ControlMsg>(welcomeLine)
            ?? throw new IOException("握手响应无效。");

        _displayLabel = displayLabel;
        ApplyStreamConfig(welcome.Width, welcome.Height, welcome.Fps);
        IsMuted = false;
        _audioPlayer.Muted = false;
        RemoteAllowsControl = welcome.AllowControl;
        IsControlling = false;
        _renderer.ControlEnabled = false;
        StateChanged?.Invoke();

        _ = Task.Run(() => TcpReadLoopAsync(reader, ct));
        _ = Task.Run(() => UdpReceiveLoopAsync(ct));

        if (autoRequestControl && RemoteAllowsControl)
        {
            await ToggleControlAsync(true);
        }
    }

    public async Task ToggleMuteAsync()
    {
        IsMuted = !IsMuted;
        _audioPlayer.Muted = IsMuted;
        await SendControlMsgAsync(new ScreenShareServer.ControlMsg { Type = "mute", Muted = IsMuted });
        StateChanged?.Invoke();
    }

    public async Task ToggleControlAsync(bool? forceState = null)
    {
        bool want = forceState ?? !IsControlling;
        if (want && !RemoteAllowsControl)
        {
            IsControlling = false;
            _renderer.ControlEnabled = false;
            StateChanged?.Invoke();
            return;
        }
        if (want)
        {
            await SendControlMsgAsync(new ScreenShareServer.ControlMsg { Type = "acquire_control" });
        }
        else
        {
            IsControlling = false;
            _renderer.ControlEnabled = false;
            await SendControlMsgAsync(new ScreenShareServer.ControlMsg { Type = "release_control" });
            StateChanged?.Invoke();
        }
    }

    private async Task TcpReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var msg = JsonSerializer.Deserialize<ScreenShareServer.ControlMsg>(line);
                if (msg is null) continue;

                if (msg.Type == "control_policy")
                {
                    RemoteAllowsControl = msg.AllowControl;
                    if (!RemoteAllowsControl)
                    {
                        IsControlling = false;
                        _renderer.ControlEnabled = false;
                    }
                    StateChanged?.Invoke();
                }
                else if (msg.Type == "control_result")
                {
                    IsControlling = msg.Granted && RemoteAllowsControl;
                    _renderer.ControlEnabled = IsControlling;
                    StateChanged?.Invoke();
                }
                else if (msg.Type == "video_config" && msg.Width >= 64 && msg.Height >= 64)
                {
                    ApplyStreamConfig(msg.Width, msg.Height, msg.Fps);
                    StateChanged?.Invoke();
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (!_cts?.IsCancellationRequested ?? false)
            {
                await DisconnectAsync();
                Disconnected?.Invoke();
            }
        }
    }

    private async Task UdpReceiveLoopAsync(CancellationToken ct)
    {
        if (_udp is null) return;

        uint currentFrameId = 0;
        int expectedSlices = 0;
        byte[]?[]? slices = null;
        int receivedCount = 0;
        DateTime lastPli = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _udp.ReceiveAsync(ct);
                var buf = res.Buffer;
                if (buf.Length < 15 || buf[0] != 0x45 || buf[1] != 0x54) continue;

                if (buf[2] == 0x02 && buf.Length > 15)
                {
                    var opus = new byte[buf.Length - 15];
                    Buffer.BlockCopy(buf, 15, opus, 0, opus.Length);
                    _audioPlayer.PlayOpusPacket(opus);
                    continue;
                }

                var packetCodec = VideoCodecs.FromPacketType(buf[2]);
                if (packetCodec is not null && buf.Length > 19)
                {
                    uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(3, 4));
                    ushort sliceIdx = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(7, 2));
                    ushort sliceCnt = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(9, 2));
                    long ptsUs = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(11, 8));

                    if (sliceCnt == 0 || sliceIdx >= sliceCnt) continue;

                    if (frameId > currentFrameId)
                    {
                        if (slices is not null && receivedCount < expectedSlices &&
                            (DateTime.UtcNow - lastPli).TotalMilliseconds > 250)
                        {
                            lastPli = DateTime.UtcNow;
                            _ = SendControlMsgAsync(new ScreenShareServer.ControlMsg { Type = "pli" });
                        }
                        currentFrameId = frameId;
                        expectedSlices = sliceCnt;
                        slices = new byte[]?[sliceCnt];
                        receivedCount = 0;
                    }
                    else if (frameId < currentFrameId || slices is null)
                    {
                        continue;
                    }

                    if (slices[sliceIdx] is null)
                    {
                        var payload = new byte[buf.Length - 19];
                        Buffer.BlockCopy(buf, 19, payload, 0, payload.Length);
                        slices[sliceIdx] = payload;
                        receivedCount++;

                        if (receivedCount == expectedSlices)
                        {
                            int total = 0;
                            for (int i = 0; i < expectedSlices; i++) total += slices[i]!.Length;
                            var frameData = new byte[total];
                            int pos = 0;
                            for (int i = 0; i < expectedSlices; i++)
                            {
                                Buffer.BlockCopy(slices[i]!, 0, frameData, pos, slices[i]!.Length);
                                pos += slices[i]!.Length;
                            }
                            slices = null;
                            DecodeAndPresent(packetCodec.Value, frameData, ptsUs * 10);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 告诉共享端本机能解哪些格式。H.265 只在有硬件解码时声明（软解 H.265 太吃 CPU），
    /// H.264 总是支持（没有硬件时软解）。
    /// </summary>
    private string[] SupportedCodecs()
    {
        _hevcHardwareSupported ??= _renderer.SupportsDecoderProfile(D3D11DecoderProfileHevcMain) &&
                                   MfVideoDecoder.ProbeD3D11(VideoCodec.Hevc, _renderer.DecoderDevice);
        return _hevcHardwareSupported == true
            ? [VideoCodecs.HevcWire, VideoCodecs.H264Wire]
            : [VideoCodecs.H264Wire];
    }

    private void ApplyStreamConfig(int width, int height, int fps)
    {
        lock (_decoderLock)
        {
            if (width >= 64 && height >= 64 && (width != _streamWidth || height != _streamHeight))
            {
                // 尺寸变了：下一帧按新尺寸重建解码器
                _decoder?.Dispose();
                _decoder = null;
            }
            if (width >= 64) _streamWidth = width;
            if (height >= 64) _streamHeight = height;
            if (fps > 0) _streamFps = fps;
        }
        UpdateRemoteLabel();
    }

    private void UpdateRemoteLabel()
    {
        string suffix = string.IsNullOrEmpty(_decodeLabel) ? string.Empty : $" · {_decodeLabel}";
        RemoteLabel = $"正在观看: {_displayLabel} ({_streamWidth}x{_streamHeight}@{_streamFps}fps{suffix})";
    }

    private void DecodeAndPresent(VideoCodec codec, byte[] annexB, long ptsHns)
    {
        string? newLabel = null;
        bool needKeyFrame = false;
        lock (_decoderLock)
        {
            if (_decoder is null || _decoder.Codec != codec)
            {
                _decoder?.Dispose();
                _decoder = CreateDecoder(codec);
                _framesWithoutOutput = 0;
                if (_decoder is null)
                {
                    if (codec == VideoCodec.Hevc && _hevcHardwareSupported != false)
                    {
                        // 本机解不了 H.265：请共享端改发 H.264
                        _hevcHardwareSupported = false;
                        _ = SendControlMsgAsync(new ScreenShareServer.ControlMsg { Type = "codecs", Codecs = [VideoCodecs.H264Wire] });
                    }
                    return;
                }
            }

            int presented;
            try
            {
                presented = _decoder.Decode(annexB, ptsHns, _renderer);
            }
            catch
            {
                presented = 0;
            }

            if (presented > 0)
            {
                _framesWithoutOutput = 0;
                string label = $"{VideoCodecs.DisplayName(codec)} {(_decoder.IsHardwareOutput ? "硬解" : "软解")}";
                if (label != _decodeLabel) newLabel = label;
            }
            else if (++_framesWithoutOutput > MaxFramesWithoutOutput)
            {
                // 长时间解不出画面：硬件 H.264 换软解；H.265 告诉共享端改发 H.264
                _framesWithoutOutput = 0;
                if (codec == VideoCodec.H264 && _decoder.UsesD3D11)
                {
                    _h264HardwareFailed = true;
                    _decoder.Dispose();
                    _decoder = null;
                    needKeyFrame = true;
                }
                else if (codec == VideoCodec.Hevc)
                {
                    _hevcHardwareSupported = false;
                    _ = SendControlMsgAsync(new ScreenShareServer.ControlMsg { Type = "codecs", Codecs = [VideoCodecs.H264Wire] });
                }
            }
        }

        if (needKeyFrame)
        {
            _ = SendControlMsgAsync(new ScreenShareServer.ControlMsg { Type = "pli" });
        }
        if (newLabel is not null)
        {
            _decodeLabel = newLabel;
            UpdateRemoteLabel();
            StateChanged?.Invoke();
        }
    }

    /// <summary>先尝试 D3D11 硬件解码，失败再软解；H.265 没有硬件解码时不软解。</summary>
    private MfVideoDecoder? CreateDecoder(VideoCodec codec)
    {
        int w = Math.Max(64, _streamWidth);
        int h = Math.Max(64, _streamHeight);
        IntPtr device = _renderer.DecoderDevice;
        bool tryHardware = device != IntPtr.Zero && (codec == VideoCodec.Hevc
            ? _hevcHardwareSupported != false
            : !_h264HardwareFailed && _renderer.SupportsDecoderProfile(D3D11DecoderProfileH264));

        if (tryHardware)
        {
            try
            {
                return MfVideoDecoder.Create(codec, w, h, device, requireD3D11: true);
            }
            catch
            {
            }
        }

        if (codec == VideoCodec.Hevc)
        {
            return null;
        }

        try
        {
            return MfVideoDecoder.Create(codec, w, h, IntPtr.Zero, requireD3D11: false);
        }
        catch
        {
            return null;
        }
    }

    private async Task SendControlMsgAsync(ScreenShareServer.ControlMsg msg)
    {
        if (_writer is null) return;
        await _tcpWriteLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(msg));
        }
        catch
        {
        }
        finally
        {
            _tcpWriteLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        IsControlling = false;
        _renderer.ControlEnabled = false;
        try { _udp?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _udp = null;
        _tcp = null;
        _writer = null;
        lock (_decoderLock)
        {
            _decoder?.Dispose();
            _decoder = null;
            _framesWithoutOutput = 0;
        }
        _decodeLabel = string.Empty;
        RemoteLabel = "未连接任何共享节点";
        _cts?.Dispose();
        _cts = null;
        StateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _audioPlayer.Dispose();
        _tcpWriteLock.Dispose();
    }
}
