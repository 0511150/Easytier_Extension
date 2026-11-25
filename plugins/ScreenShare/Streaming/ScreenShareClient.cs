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
    private MfH264Decoder? _decoder;
    private CancellationTokenSource? _cts;
    private byte[] _bgraScratch = [];
    private string _displayLabel = string.Empty;

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
            UdpPort = localUdpPort
        });

        var welcomeLine = await reader.ReadLineAsync(ct);
        if (welcomeLine is null)
        {
            throw new IOException("远端未响应握手。");
        }

        var welcome = JsonSerializer.Deserialize<ScreenShareServer.ControlMsg>(welcomeLine)
            ?? throw new IOException("握手响应无效。");

        _displayLabel = displayLabel;
        _decoder = new MfH264Decoder(Math.Max(64, welcome.Width), Math.Max(64, welcome.Height));
        RemoteLabel = $"正在观看: {displayLabel} ({welcome.Width}x{welcome.Height}@{welcome.Fps}fps)";
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
                    if (_decoder is null || _decoder.Width != msg.Width || _decoder.Height != msg.Height)
                    {
                        _decoder?.Dispose();
                        _decoder = new MfH264Decoder(msg.Width, msg.Height);
                    }
                    RemoteLabel = $"正在观看: {_displayLabel} ({msg.Width}x{msg.Height}@{msg.Fps}fps)";
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

                if (buf[2] == 0x01 && buf.Length > 19)
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
                            DecodeAndPresent(frameData, ptsUs * 10);
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

    private unsafe void DecodeAndPresent(byte[] annexB, long ptsHns)
    {
        if (_decoder is null) return;
        var nv12 = _decoder.Decode(annexB, ptsHns);
        if (nv12 is null) return;

        int w = _decoder.Width;
        int h = _decoder.Height;
        int needed = w * h * 4;
        if (_bgraScratch.Length != needed)
        {
            _bgraScratch = new byte[needed];
        }

        fixed (byte* pNv12 = nv12)
        fixed (byte* pBgra = _bgraScratch)
        {
            ColorConvert.Nv12ToBgra(pNv12, w, h, pBgra, w * 4);
        }

        _renderer.PresentBgraFrame(_bgraScratch, w, h);
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
        _decoder?.Dispose();
        _decoder = null;
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
