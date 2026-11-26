using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PeerSpeedTest.Engine;

/// <summary>
/// 测速服务端：只监听各虚拟网络的本机虚拟 IP，因此只有组网内节点能访问。
/// 绑定地址按差量更新，网络状态变化时不会打断正在进行的测速。
/// </summary>
public sealed class SpeedTestServer : IAsyncDisposable
{
    public const int MaxConcurrentConnections = 32;
    private const int HandshakeTimeoutMs = 5_000;
    private const int PingSessionLimitMs = 60_000;

    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(SpeedProtocol.BufferSize);

    private readonly int _port;
    private readonly ConcurrentDictionary<IPAddress, TcpListener> _listeners = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _bindLock = new();
    private int _active;

    public SpeedTestServer(int port = SpeedProtocol.DefaultPort)
    {
        _port = port;
    }

    /// <summary>当前正在被测速的连接数（不含握手中的连接）。</summary>
    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>最近一次绑定失败的说明（例如端口被占用）；全部成功时为 null。</summary>
    public string? LastBindError { get; private set; }

    public int ListeningCount => _listeners.Count;

    public event Action? ActivityChanged;

    /// <summary>端口为 0 时返回系统实际分配的端口（测试用）。</summary>
    public int GetBoundPort(IPAddress address) =>
        _listeners.TryGetValue(address, out var l) ? ((IPEndPoint)l.LocalEndpoint).Port : 0;

    public void SetBindAddresses(IEnumerable<IPAddress> addresses)
    {
        lock (_bindLock)
        {
            if (_cts.IsCancellationRequested) return;
            var wanted = addresses.Distinct().ToHashSet();

            foreach (var (ip, listener) in _listeners)
            {
                if (wanted.Contains(ip)) continue;
                if (_listeners.TryRemove(ip, out _))
                {
                    try { listener.Stop(); } catch { }
                }
            }

            string? error = null;
            foreach (var ip in wanted)
            {
                if (_listeners.ContainsKey(ip)) continue;
                try
                {
                    var listener = new TcpListener(ip, _port);
                    listener.Start(64);
                    _listeners[ip] = listener;
                    _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    error = $"端口 {_port} 已被占用";
                }
                catch (SocketException ex)
                {
                    // 网卡刚创建时地址可能尚未生效，下次网络状态变化会再次尝试
                    error ??= $"无法监听 {ip}：{ex.SocketErrorCode}";
                }
            }
            LastBindError = error;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(ct);
            }
            catch
            {
                return; // 监听已停止
            }
            _ = Task.Run(() => HandleConnectionAsync(socket, ct));
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken serverCt)
    {
        bool counted = false;
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        try
        {
            socket.NoDelay = true;
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            using var reg = connCts.Token.Register(() => { try { socket.Dispose(); } catch { } });

            connCts.CancelAfter(HandshakeTimeoutMs);
            var request = await SpeedProtocol.ReadLineAsync<SpeedRequest>(stream, connCts.Token);
            if (request is null || request.Magic != SpeedProtocol.Magic) return;

            if (Interlocked.Increment(ref _active) > MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _active);
                await SpeedProtocol.WriteLineAsync(stream, new SpeedReply { Ok = false, Error = "busy" }, connCts.Token);
                return;
            }
            counted = true;
            ActivityChanged?.Invoke();

            int durationMs = SpeedProtocol.ClampDuration(request.DurationMs);
            switch (request.Op)
            {
                case SpeedProtocol.OpPing:
                    connCts.CancelAfter(PingSessionLimitMs);
                    await ReplyOkAsync(stream, connCts.Token);
                    await EchoAsync(stream, connCts.Token);
                    break;
                case SpeedProtocol.OpDownload:
                    connCts.CancelAfter(durationMs + 10_000);
                    await ReplyOkAsync(stream, connCts.Token);
                    await SendForAsync(socket, stream, durationMs, connCts.Token);
                    break;
                case SpeedProtocol.OpUpload:
                    connCts.CancelAfter(durationMs + 15_000);
                    await ReplyOkAsync(stream, connCts.Token);
                    await ReceiveAndReportAsync(stream, durationMs, connCts.Token);
                    break;
                default:
                    await SpeedProtocol.WriteLineAsync(stream, new SpeedReply { Ok = false, Error = "unsupported_op" }, connCts.Token);
                    break;
            }
        }
        catch
        {
            // 对方断开、超时或数据错误：直接结束该连接
        }
        finally
        {
            if (counted)
            {
                Interlocked.Decrement(ref _active);
                ActivityChanged?.Invoke();
            }
        }
    }

    private static Task ReplyOkAsync(Stream stream, CancellationToken ct) =>
        SpeedProtocol.WriteLineAsync(stream, new SpeedReply { Ok = true, Version = SpeedProtocol.Version }, ct);

    private static async Task EchoAsync(Stream stream, CancellationToken ct)
    {
        var frame = new byte[SpeedProtocol.PingFrameSize];
        while (true)
        {
            try
            {
                await stream.ReadExactlyAsync(frame, ct);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            await stream.WriteAsync(frame, ct);
        }
    }

    private static async Task SendForAsync(Socket socket, Stream stream, int durationMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            await stream.WriteAsync(Payload, ct);
        }
        socket.Shutdown(SocketShutdown.Send);

        // 等客户端读完并关闭，避免直接关闭时丢弃尚未发出的数据
        var drain = new byte[256];
        while (await stream.ReadAsync(drain, ct) > 0) { }
    }

    private static async Task ReceiveAndReportAsync(Stream stream, int durationMs, CancellationToken ct)
    {
        var buffer = new byte[SpeedProtocol.BufferSize];
        int warmupMs = SpeedProtocol.WarmupMs(durationMs);
        Stopwatch? sw = null;
        long total = 0, bytesAtWarmup = -1;
        double msAtWarmup = 0;

        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            sw ??= Stopwatch.StartNew();
            if (bytesAtWarmup < 0 && sw.ElapsedMilliseconds >= warmupMs)
            {
                bytesAtWarmup = total;
                msAtWarmup = sw.Elapsed.TotalMilliseconds;
            }
            total += n;
        }

        double elapsed = sw?.Elapsed.TotalMilliseconds ?? 0;
        var reply = bytesAtWarmup >= 0 && elapsed - msAtWarmup > 0
            ? new SpeedReply { Ok = true, Bytes = total - bytesAtWarmup, Ms = elapsed - msAtWarmup }
            : new SpeedReply { Ok = true, Bytes = total, Ms = elapsed };
        await SpeedProtocol.WriteLineAsync(stream, reply, ct);
    }

    public ValueTask DisposeAsync()
    {
        lock (_bindLock)
        {
            _cts.Cancel();
            foreach (var l in _listeners.Values)
            {
                try { l.Stop(); } catch { }
            }
            _listeners.Clear();
        }
        // 不释放 _cts：仍在收尾的连接会用它创建关联令牌
        return ValueTask.CompletedTask;
    }
}
