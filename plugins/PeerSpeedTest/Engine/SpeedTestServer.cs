using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PeerSpeedTest.Engine;

/// <summary>正在进行的被测速会话（被测方视角）。</summary>
public sealed record SpeedSessionInfo(string Hostname, IPAddress RemoteIp, DateTime StartedAt);

/// <summary>
/// 测速服务端：只监听各虚拟网络的本机虚拟 IP，因此只有组网内节点能访问。
/// 每次测速都要经过本机用户同意，同一时间只服务一个发起方。
/// 绑定地址按差量更新，网络状态变化时不会打断正在进行的测速。
/// </summary>
public sealed class SpeedTestServer : IAsyncDisposable
{
    public const int MaxConcurrentConnections = 32;
    private const int HandshakeTimeoutMs = 5_000;
    private const int SessionLimitMs = 120_000;

    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(SpeedProtocol.BufferSize);

    private readonly int _port;
    private readonly Func<ConsentRequest, CancellationToken, Task<bool>> _consent;
    private readonly ConcurrentDictionary<IPAddress, TcpListener> _listeners = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _bindLock = new();
    private readonly object _sessionLock = new();
    private int _active;

    private bool _consentPending;
    private Session? _session;

    /// <param name="consent">收到测速请求时询问本机用户；返回 true 表示同意。令牌取消表示发起方已放弃。</param>
    public SpeedTestServer(Func<ConsentRequest, CancellationToken, Task<bool>> consent, int port = SpeedProtocol.DefaultPort)
    {
        _consent = consent;
        _port = port;
    }

    /// <summary>当前正在传输的数据连接数。</summary>
    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>最近一次绑定失败的说明（例如端口被占用）；全部成功时为 null。</summary>
    public string? LastBindError { get; private set; }

    public int ListeningCount => _listeners.Count;

    public SpeedSessionInfo? CurrentSession
    {
        get
        {
            lock (_sessionLock) return _session?.Info;
        }
    }

    /// <summary>会话开始、结束或连接数变化时触发。</summary>
    public event Action? ActivityChanged;

    /// <summary>端口为 0 时返回系统实际分配的端口（测试用）。</summary>
    public int GetBoundPort(IPAddress address) =>
        _listeners.TryGetValue(address, out var l) ? ((IPEndPoint)l.LocalEndpoint).Port : 0;

    /// <summary>被测方中止当前测速：关闭控制连接与全部数据连接。</summary>
    public void AbortSession()
    {
        lock (_sessionLock) _session?.Cts.Cancel();
    }

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
        var remoteIp = Normalize(((IPEndPoint)socket.RemoteEndPoint!).Address);
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        try
        {
            socket.NoDelay = true;
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            using var reg = connCts.Token.Register(() => { try { socket.Dispose(); } catch { } });

            connCts.CancelAfter(HandshakeTimeoutMs);
            var request = await SpeedProtocol.ReadLineAsync<SpeedRequest>(stream, connCts.Token);
            if (request is null || request.Magic != SpeedProtocol.Magic) return;

            if (request.Op == SpeedProtocol.OpRequest)
            {
                await HandleSessionRequestAsync(stream, request, remoteIp, connCts);
                return;
            }

            if (request.Op is not (SpeedProtocol.OpPing or SpeedProtocol.OpDownload or SpeedProtocol.OpUpload))
            {
                await ReplyErrorAsync(stream, SpeedProtocol.ErrorUnsupported, connCts.Token);
                return;
            }

            Session? session;
            lock (_sessionLock) session = _session;
            if (session is null || request.Token != session.Token || !session.Info.RemoteIp.Equals(remoteIp))
            {
                await ReplyErrorAsync(stream, SpeedProtocol.ErrorUnauthorized, connCts.Token);
                return;
            }

            await HandleDataAsync(socket, stream, request, session, connCts);
        }
        catch
        {
            // 对方断开、超时、被中止或数据错误：直接结束该连接
        }
    }

    private async Task HandleSessionRequestAsync(NetworkStream stream, SpeedRequest request, IPAddress remoteIp, CancellationTokenSource connCts)
    {
        bool busy;
        lock (_sessionLock)
        {
            busy = _consentPending || _session is not null;
            if (!busy) _consentPending = true;
        }
        if (busy)
        {
            await ReplyErrorAsync(stream, SpeedProtocol.ErrorBusy, connCts.Token);
            return;
        }

        Session? session = null;
        try
        {
            connCts.CancelAfter(SpeedProtocol.ConsentTimeoutMs + 5_000);

            // 发起方在等待期间断开时撤回确认提示
            var watch = new byte[1];
            var peerClosed = stream.ReadAsync(watch, connCts.Token).AsTask();
            using var askCts = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token);
            askCts.CancelAfter(SpeedProtocol.ConsentTimeoutMs);
            _ = peerClosed.ContinueWith(_ =>
            {
                try { askCts.Cancel(); } catch (ObjectDisposedException) { }
            }, TaskScheduler.Default);

            string hostname = string.IsNullOrWhiteSpace(request.Hostname) ? remoteIp.ToString() : request.Hostname.Trim();
            if (hostname.Length > 64) hostname = hostname[..64];
            bool accepted;
            try
            {
                accepted = await _consent(new ConsentRequest(hostname, remoteIp), askCts.Token);
            }
            catch (OperationCanceledException)
            {
                accepted = false;
            }

            if (peerClosed.IsCompleted) return;
            if (!accepted)
            {
                await ReplyErrorAsync(stream, askCts.IsCancellationRequested ? SpeedProtocol.ErrorTimeout : SpeedProtocol.ErrorRejected, connCts.Token);
                return;
            }

            session = new Session(Guid.NewGuid().ToString("N"), new SpeedSessionInfo(hostname, remoteIp, DateTime.Now), connCts);
            lock (_sessionLock)
            {
                _consentPending = false;
                _session = session;
            }
            ActivityChanged?.Invoke();

            connCts.CancelAfter(SessionLimitMs);
            await SpeedProtocol.WriteLineAsync(stream, new SpeedReply { Ok = true, Version = SpeedProtocol.Version, Token = session.Token }, connCts.Token);

            // 控制连接保持到发起方测完关闭；会话被中止时 connCts 取消，数据连接随之关闭
            await peerClosed;
        }
        finally
        {
            lock (_sessionLock)
            {
                _consentPending = false;
                if (ReferenceEquals(_session, session)) _session = null;
            }
            if (session is not null)
            {
                try { connCts.Cancel(); } catch (ObjectDisposedException) { }
                ActivityChanged?.Invoke();
            }
        }
    }

    private async Task HandleDataAsync(Socket socket, NetworkStream stream, SpeedRequest request, Session session, CancellationTokenSource connCts)
    {
        if (Interlocked.Increment(ref _active) > MaxConcurrentConnections)
        {
            Interlocked.Decrement(ref _active);
            await ReplyErrorAsync(stream, SpeedProtocol.ErrorBusy, connCts.Token);
            return;
        }

        // 会话结束（中止、超时或发起方断开）时关闭该数据连接
        using var sessionReg = session.Cts.Token.Register(() => { try { connCts.Cancel(); } catch (ObjectDisposedException) { } });
        ActivityChanged?.Invoke();
        try
        {
            int durationMs = SpeedProtocol.ClampDuration(request.DurationMs);
            switch (request.Op)
            {
                case SpeedProtocol.OpPing:
                    connCts.CancelAfter(SessionLimitMs);
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
                    await ReceiveAndReportAsync(stream, connCts.Token);
                    break;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            ActivityChanged?.Invoke();
        }
    }

    private static Task ReplyOkAsync(Stream stream, CancellationToken ct) =>
        SpeedProtocol.WriteLineAsync(stream, new SpeedReply { Ok = true, Version = SpeedProtocol.Version }, ct);

    private static async Task ReplyErrorAsync(Stream stream, string error, CancellationToken ct)
    {
        try
        {
            await SpeedProtocol.WriteLineAsync(stream, new SpeedReply { Ok = false, Error = error, Version = SpeedProtocol.Version }, ct);
        }
        catch
        {
        }
    }

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

        // 等发起方读完并关闭，避免直接关闭时丢弃尚未发出的数据
        var drain = new byte[256];
        while (await stream.ReadAsync(drain, ct) > 0) { }
    }

    private static async Task ReceiveAndReportAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[SpeedProtocol.BufferSize];
        Stopwatch? sw = null;
        long total = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            sw ??= Stopwatch.StartNew();
            total += n;
        }
        await SpeedProtocol.WriteLineAsync(stream, new SpeedReply { Ok = true, Bytes = total, Ms = sw?.Elapsed.TotalMilliseconds ?? 0 }, ct);
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

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

    private sealed record Session(string Token, SpeedSessionInfo Info, CancellationTokenSource Cts);
}
