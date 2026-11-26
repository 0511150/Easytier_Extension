using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MouseShare.Engine;

public sealed record PairRequestInfo(string DeviceId, string Name, IPAddress RemoteIp);

public enum PairOutcome
{
    Accepted,
    Rejected,
    Failed
}

/// <summary>
/// 设备连接中心：只监听各虚拟网络的本机虚拟 IP；与每台已配对设备保持一条双向连接（双方同时连上时保留 id 较小一方发起的那条）。
/// 未配对的设备只能发起配对请求，必须经本机用户同意。
/// </summary>
public sealed class PeerHub : IAsyncDisposable
{
    private readonly string _localId;
    private readonly Func<string> _localName;
    private readonly Func<string, byte[]?> _getKey;
    private readonly int _port;
    private readonly ConcurrentDictionary<IPAddress, TcpListener> _listeners = new();
    private readonly ConcurrentDictionary<string, PeerSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _dialing = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _bindLock = new();
    private readonly object _sessionLock = new();
    private int _pairPending;

    public PeerHub(string localId, Func<string> localName, Func<string, byte[]?> getKey, int port = Protocol.DefaultPort)
    {
        _localId = localId;
        _localName = localName;
        _getKey = getKey;
        _port = port;
        _ = Task.Run(HeartbeatLoopAsync);
    }

    /// <summary>收到配对请求时询问本机用户，返回 true 表示同意。</summary>
    public Func<PairRequestInfo, CancellationToken, Task<bool>>? ConsentAsync { get; set; }

    /// <summary>本机同意配对后保存对方（id、名称、密钥）。</summary>
    public Action<string, string, byte[]>? Paired { get; set; }

    /// <summary>连接对方时被告知“未配对”（对方已移除本机）。</summary>
    public Action<string>? RemoteUnpaired { get; set; }

    public event Action<PeerSession>? SessionOpened;
    public event Action<PeerSession>? SessionClosed;
    public event Action<PeerSession, MsgType, byte[]>? MessageReceived;

    public string? LastBindError { get; private set; }
    public int ListeningCount => _listeners.Count;

    public int GetBoundPort(IPAddress address) =>
        _listeners.TryGetValue(address, out var l) ? ((IPEndPoint)l.LocalEndpoint).Port : 0;

    public bool IsConnected(string peerId) => _sessions.TryGetValue(peerId, out var s) && !s.IsClosed;

    public IReadOnlyCollection<string> ConnectedIds => _sessions.Where(kv => !kv.Value.IsClosed).Select(kv => kv.Key).ToList();

    public bool Send(string peerId, byte[] frame) => _sessions.TryGetValue(peerId, out var s) && s.Send(frame);

    public void Broadcast(byte[] frame)
    {
        foreach (var s in _sessions.Values) s.Send(frame);
    }

    public void Disconnect(string peerId)
    {
        if (_sessions.TryGetValue(peerId, out var s)) s.Close();
    }

    // ---------------- 监听 ----------------

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
                    listener.Start();
                    _listeners[ip] = listener;
                    _ = Task.Run(() => AcceptLoopAsync(listener));
                }
                catch (SocketException ex)
                {
                    error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                        ? $"TCP {_port} 端口已被占用"
                        : $"无法监听 {ip}:{_port}（{ex.SocketErrorCode}）";
                }
            }
            LastBindError = error;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return; // 监听已停止
            }
            _ = Task.Run(() => HandleIncomingAsync(client));
        }
    }

    private async Task HandleIncomingAsync(TcpClient client)
    {
        bool keep = false;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(Protocol.HandshakeTimeoutMs);

            var nonceS = Protocol.Nonce();
            await WriteAsync(stream, Protocol.JsonFrame(MsgType.Challenge, new ChallengeMsg { Id = _localId, Nonce = Convert.ToBase64String(nonceS) }), timeout.Token);
            var (type, payload) = await Protocol.ReadFrameAsync(stream, timeout.Token);

            if (type == MsgType.Auth)
            {
                var auth = Protocol.ParseJson<AuthMsg>(payload);
                if (auth is null || !Protocol.IsValidId(auth.Id) || auth.Id == _localId) return;
                var key = _getKey(auth.Id);
                if (key is null)
                {
                    await WriteAsync(stream, Protocol.JsonFrame(MsgType.AuthFail, new AuthFailMsg { Reason = AuthFailMsg.Unpaired }), timeout.Token);
                    return;
                }
                byte[] nonceC = Convert.FromBase64String(auth.Nonce);
                if (nonceC.Length != 16 || !Protocol.MacEquals(Protocol.Mac(key, "C", nonceS, nonceC, auth.Id, _localId), auth.Mac))
                {
                    await WriteAsync(stream, Protocol.JsonFrame(MsgType.AuthFail, new AuthFailMsg { Reason = AuthFailMsg.BadMac }), timeout.Token);
                    return;
                }
                var mac = Protocol.Mac(key, "S", nonceC, nonceS, _localId, auth.Id);
                await WriteAsync(stream, Protocol.JsonFrame(MsgType.AuthOk, new AuthOkMsg { Mac = Convert.ToBase64String(mac) }), timeout.Token);
                keep = Register(new PeerSession(auth.Id, isDialer: false, client, stream));
            }
            else if (type == MsgType.PairRequest)
            {
                await HandlePairRequestAsync(client, stream, payload);
            }
        }
        catch
        {
        }
        finally
        {
            if (!keep) client.Dispose();
        }
    }

    private async Task HandlePairRequestAsync(TcpClient client, NetworkStream stream, byte[] payload)
    {
        var req = Protocol.ParseJson<PairRequestMsg>(payload);
        byte[] key;
        try
        {
            key = req is null ? [] : Convert.FromBase64String(req.Key);
        }
        catch (FormatException)
        {
            return;
        }
        if (req is null || !Protocol.IsValidId(req.Id) || req.Id == _localId || key.Length != Protocol.KeyBytes) return;

        bool ok;
        var existing = _getKey(req.Id);
        if (existing is not null && existing.AsSpan().SequenceEqual(key))
        {
            ok = true; // 已配对过（对方重新请求），无需再次确认
        }
        else if (ConsentAsync is null || Interlocked.CompareExchange(ref _pairPending, 1, 0) != 0)
        {
            ok = false; // 同一时间只处理一个配对请求
        }
        else
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                wait.CancelAfter(Protocol.ConsentTimeoutMs);
                var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                string name = string.IsNullOrWhiteSpace(req.Name) ? ip.ToString() : req.Name.Trim();
                if (name.Length > 64) name = name[..64];
                ok = await ConsentAsync(new PairRequestInfo(req.Id, name, ip), wait.Token);
            }
            catch
            {
                ok = false;
            }
            finally
            {
                Interlocked.Exchange(ref _pairPending, 0);
            }
        }

        if (ok) Paired?.Invoke(req.Id, req.Name, key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(Protocol.HandshakeTimeoutMs);
        await WriteAsync(stream, Protocol.JsonFrame(MsgType.PairResult, new PairResultMsg { Ok = ok, Id = _localId, Name = _localName() }), timeout.Token);
    }

    // ---------------- 主动连接 ----------------

    /// <summary>未连接且没有正在进行的连接时，在后台连接对方。</summary>
    public void Connect(string peerId, IPEndPoint endpoint)
    {
        if (IsConnected(peerId) || _cts.IsCancellationRequested) return;
        if (!_dialing.TryAdd(peerId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await DialAsync(peerId, endpoint);
            }
            catch
            {
            }
            finally
            {
                _dialing.TryRemove(peerId, out _);
            }
        });
    }

    private async Task DialAsync(string peerId, IPEndPoint endpoint)
    {
        var key = _getKey(peerId);
        if (key is null) return;

        var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
        bool keep = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(Protocol.ConnectTimeoutMs + Protocol.HandshakeTimeoutMs);
            await client.ConnectAsync(endpoint, timeout.Token);
            var stream = client.GetStream();

            var challenge = await ReadChallengeAsync(stream, peerId, timeout.Token);
            if (challenge is null) return;

            var nonceC = Protocol.Nonce();
            var mac = Protocol.Mac(key, "C", challenge, nonceC, _localId, peerId);
            await WriteAsync(stream, Protocol.JsonFrame(MsgType.Auth, new AuthMsg
            {
                Id = _localId,
                Nonce = Convert.ToBase64String(nonceC),
                Mac = Convert.ToBase64String(mac)
            }), timeout.Token);

            var (type, payload) = await Protocol.ReadFrameAsync(stream, timeout.Token);
            if (type == MsgType.AuthFail)
            {
                if (Protocol.ParseJson<AuthFailMsg>(payload)?.Reason == AuthFailMsg.Unpaired) RemoteUnpaired?.Invoke(peerId);
                return;
            }
            if (type != MsgType.AuthOk) return;
            var ok = Protocol.ParseJson<AuthOkMsg>(payload);
            if (ok is null || !Protocol.MacEquals(Protocol.Mac(key, "S", nonceC, challenge, peerId, _localId), ok.Mac)) return;

            keep = Register(new PeerSession(peerId, isDialer: true, client, stream));
        }
        finally
        {
            if (!keep) client.Dispose();
        }
    }

    /// <summary>向对方发起配对：对方用户同意后双方保存同一个密钥。</summary>
    public async Task<(PairOutcome Outcome, string Name)> RequestPairAsync(string peerId, IPEndPoint endpoint, byte[] key, CancellationToken ct)
    {
        using var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(Protocol.ConnectTimeoutMs + Protocol.ConsentTimeoutMs + Protocol.HandshakeTimeoutMs);
            await client.ConnectAsync(endpoint, timeout.Token);
            var stream = client.GetStream();
            if (await ReadChallengeAsync(stream, peerId, timeout.Token) is null) return (PairOutcome.Failed, string.Empty);

            await WriteAsync(stream, Protocol.JsonFrame(MsgType.PairRequest, new PairRequestMsg
            {
                Id = _localId,
                Name = _localName(),
                Key = Convert.ToBase64String(key)
            }), timeout.Token);

            var (type, payload) = await Protocol.ReadFrameAsync(stream, timeout.Token);
            var result = type == MsgType.PairResult ? Protocol.ParseJson<PairResultMsg>(payload) : null;
            if (result is null || result.Id != peerId) return (PairOutcome.Failed, string.Empty);
            return (result.Ok ? PairOutcome.Accepted : PairOutcome.Rejected, result.Name);
        }
        catch
        {
            return (PairOutcome.Failed, string.Empty);
        }
    }

    private static async Task<byte[]?> ReadChallengeAsync(NetworkStream stream, string expectedId, CancellationToken ct)
    {
        var (type, payload) = await Protocol.ReadFrameAsync(stream, ct);
        if (type != MsgType.Challenge) return null;
        var ch = Protocol.ParseJson<ChallengeMsg>(payload);
        // 对方必须是预期的设备，防止旧 IP 已被其他设备使用
        if (ch is null || ch.Id != expectedId || ch.Version != Protocol.Version) return null;
        try
        {
            var nonce = Convert.FromBase64String(ch.Nonce);
            return nonce.Length == 16 ? nonce : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(NetworkStream stream, byte[] frame, CancellationToken ct)
    {
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    // ---------------- 会话管理 ----------------

    private bool Register(PeerSession session)
    {
        PeerSession? replaced = null;
        lock (_sessionLock)
        {
            if (_cts.IsCancellationRequested) return false;
            if (_sessions.TryGetValue(session.PeerId, out var existing) && !existing.IsClosed)
            {
                // 双方同时连上：保留 id 较小一方发起的连接，两端规则一致
                bool localIsSmaller = string.CompareOrdinal(_localId, session.PeerId) < 0;
                bool existingPreferred = existing.IsDialer == localIsSmaller;
                if (existingPreferred) return false;
                replaced = existing;
            }
            _sessions[session.PeerId] = session;
        }

        session.Start(OnMessage, OnClosed);
        replaced?.Close();
        SessionOpened?.Invoke(session);
        return true;
    }

    private void OnMessage(PeerSession session, MsgType type, byte[] payload)
    {
        switch (type)
        {
            case MsgType.Ping:
                session.Send(Protocol.Frame(MsgType.Pong));
                return;
            case MsgType.Pong:
                return;
        }
        if (_sessions.TryGetValue(session.PeerId, out var current) && current == session)
        {
            MessageReceived?.Invoke(session, type, payload);
        }
    }

    private void OnClosed(PeerSession session)
    {
        bool removed;
        lock (_sessionLock)
        {
            removed = _sessions.TryGetValue(session.PeerId, out var current) && current == session &&
                      _sessions.TryRemove(session.PeerId, out _);
        }
        if (removed) SessionClosed?.Invoke(session);
    }

    private async Task HeartbeatLoopAsync()
    {
        var ping = Protocol.Frame(MsgType.Ping);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Protocol.PingIntervalMs, _cts.Token);
            }
            catch
            {
                return;
            }
            foreach (var s in _sessions.Values)
            {
                if (s.IdleMs > Protocol.DeadAfterMs) s.Close();
                else s.Send(ping);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sessionLock) _cts.Cancel();
        lock (_bindLock)
        {
            foreach (var l in _listeners.Values)
            {
                try { l.Stop(); } catch { }
            }
            _listeners.Clear();
        }
        foreach (var s in _sessions.Values) s.Close();
        return ValueTask.CompletedTask;
    }
}
