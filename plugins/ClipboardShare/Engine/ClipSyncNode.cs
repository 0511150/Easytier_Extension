using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ClipboardShare.Engine;

/// <summary>组网内另一台运行本插件的设备的一个可达地址（同一设备在多个网络中会有多个）。</summary>
public sealed record PeerEndpoint(string DeviceId, string Name, IPAddress Address, int Port, IPAddress? LocalAddress, string NetworkName);

/// <summary>其他设备第一次请求共享时，交给本机用户确认的信息。</summary>
public sealed record PairRequestInfo(string DeviceId, string Name, IPAddress RemoteIp);

/// <summary>收到并已写入历史的记录。Merge 为 true 表示是连接（重连）后补发的一批。</summary>
public sealed record ClipsReceivedEventArgs(string DeviceId, string Name, IReadOnlyList<ClipEntry> Entries, bool Merge);

public enum LinkState
{
    /// <summary>不向该设备发送（未配对、已暂停或离线）。</summary>
    Idle,
    Connecting,
    Connected,
    /// <summary>连接失败或断开，稍后自动重连。</summary>
    Retrying
}

public enum PairResult
{
    Accepted,
    Rejected,
    Timeout,
    Busy,
    Paused,
    Unreachable,
    Cancelled,
    Failed
}

public sealed record PeerStatus(
    string DeviceId,
    string Name,
    IReadOnlyList<PeerEndpoint> Endpoints,
    PairingRecord? Pairing,
    LinkState Link,
    string? Error,
    bool Receiving,
    bool PairingInProgress)
{
    public bool Online => Endpoints.Count > 0;
}

/// <summary>
/// 剪贴板同步节点：
/// - 服务端只监听本机各虚拟网络的虚拟 IP，接收已配对设备发来的内容；未配对设备首次请求时询问本机用户。
/// - 对每台已配对且开启发送的在线设备保持一条发送连接：连接（含掉线重连）后先补发对方缺少的本机记录，之后复制即发。
/// </summary>
public sealed class ClipSyncNode : IAsyncDisposable
{
    private readonly string _deviceId;
    private readonly Func<string> _localName;
    private readonly PairingStore _pairs;
    private readonly ClipHistory _history;
    private readonly Func<PairRequestInfo, CancellationToken, Task<bool>> _consent;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, TcpListener> _listeners = new();
    private readonly Dictionary<string, List<PeerEndpoint>> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeerLink> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _pendingPairKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<CancellationTokenSource>> _inbound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private bool _consentPending;
    private bool _syncEnabled = true;
    private bool _disposed;

    /// <param name="consent">未配对设备请求共享时询问本机用户，返回 true 表示同意；令牌取消表示对方已放弃或超时。</param>
    public ClipSyncNode(
        string deviceId,
        Func<string> localName,
        PairingStore pairs,
        ClipHistory history,
        Func<PairRequestInfo, CancellationToken, Task<bool>> consent,
        int port = ClipProtocol.DefaultPort)
    {
        _deviceId = deviceId;
        _localName = localName;
        _pairs = pairs;
        _history = history;
        _consent = consent;
        _port = port;
    }

    public string DeviceId => _deviceId;

    /// <summary>最近一次监听失败的说明（例如端口被占用）；全部成功时为 null。</summary>
    public string? LastBindError { get; private set; }

    public int ListeningCount
    {
        get
        {
            lock (_lock) return _listeners.Count;
        }
    }

    public bool SyncEnabled
    {
        get
        {
            lock (_lock) return _syncEnabled;
        }
    }

    /// <summary>收到的内容是否接受（例如关闭图片同步时拒收图片）；为 null 时全部接受。</summary>
    public Func<ClipEntry, bool>? IncomingFilter { get; set; }

    /// <summary>连接、配对或设备列表变化时触发（可能很频繁，界面应自行合并刷新）。</summary>
    public event Action? StateChanged;

    /// <summary>收到其他设备的内容并已写入历史。在网络线程上触发，处理函数不要阻塞。</summary>
    public event Action<ClipsReceivedEventArgs>? ClipsReceived;

    // ---------------- 对外操作 ----------------

    /// <summary>端口为 0 时返回系统实际分配的端口（测试用）。</summary>
    public int GetBoundPort(IPAddress address)
    {
        lock (_lock) return _listeners.TryGetValue(address, out var l) ? ((IPEndPoint)l.LocalEndpoint).Port : 0;
    }

    /// <summary>按差量更新监听地址，网络变化时不会打断已有连接。</summary>
    public void SetBindAddresses(IEnumerable<IPAddress> addresses)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var wanted = addresses.Distinct().ToHashSet();
            foreach (var (ip, listener) in _listeners.ToList())
            {
                if (wanted.Contains(ip)) continue;
                _listeners.Remove(ip);
                try { listener.Stop(); } catch { }
            }

            string? error = null;
            foreach (var ip in wanted)
            {
                if (_listeners.ContainsKey(ip)) continue;
                try
                {
                    var listener = new TcpListener(ip, _port);
                    listener.Start(32);
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
        RaiseStateChanged();
    }

    /// <summary>用服务发现结果整体替换在线设备的地址列表（排在前面的地址优先连接）。</summary>
    public void UpdateEndpoints(IEnumerable<PeerEndpoint> endpoints)
    {
        lock (_lock)
        {
            _endpoints.Clear();
            foreach (var group in endpoints.Where(e => e.DeviceId != _deviceId).GroupBy(e => e.DeviceId, StringComparer.Ordinal))
            {
                _endpoints[group.Key] = group.ToList();
            }
        }
        Reconcile();
    }

    /// <summary>总开关：关闭后不发送也不接收（新连接回复“已暂停”）。</summary>
    public void SetSyncEnabled(bool enabled)
    {
        List<CancellationTokenSource> toClose = [];
        lock (_lock)
        {
            _syncEnabled = enabled;
            if (!enabled) toClose = _inbound.Values.SelectMany(s => s).ToList();
        }
        foreach (var cts in toClose) TryCancel(cts);
        Reconcile();
    }

    /// <summary>是否向该设备发送。重新开启后只补发开启之后复制的内容。</summary>
    public void SetSendEnabled(string deviceId, bool enabled)
    {
        _pairs.Update(deviceId, r => r with
        {
            SendEnabled = enabled,
            SendEnabledSinceUtc = enabled && !r.SendEnabled ? DateTime.UtcNow : r.SendEnabledSinceUtc
        });
        Reconcile();
    }

    /// <summary>本机复制了新内容（已写入历史）：立即发送给所有已连接的设备，未连接的设备在连接后补发。</summary>
    public void Publish(ClipEntry entry)
    {
        if (entry.LocalOnly || entry.OriginDeviceId != _deviceId) return;
        lock (_lock)
        {
            if (!_syncEnabled) return;
            foreach (var link in _links.Values) link.Queue.Writer.TryWrite(entry);
        }
    }

    /// <summary>向未配对的设备发起共享，对方在其界面同意后双方完成配对。</summary>
    public async Task<PairResult> RequestPairAsync(string deviceId, CancellationToken ct = default)
    {
        byte[] key = ClipProtocol.NewKey();
        List<PeerEndpoint> endpoints;
        lock (_lock)
        {
            if (_pendingPairKeys.ContainsKey(deviceId)) return PairResult.Busy;
            if (!_syncEnabled) return PairResult.Paused;
            _pendingPairKeys[deviceId] = key;
            _errors.Remove(deviceId);
            endpoints = _endpoints.GetValueOrDefault(deviceId)?.ToList() ?? [];
        }
        RaiseStateChanged();

        try
        {
            foreach (var ep in endpoints)
            {
                try
                {
                    using var socket = await ConnectAsync(ep, ct);
                    await using var stream = new NetworkStream(socket, ownsSocket: false);
                    var hs = await HandshakeAsync(stream, deviceId, proofKey: null, pairKey: key, unpair: false, ct);
                    ConfirmPairing(deviceId, ClipProtocol.SanitizeName(hs.Challenge.Name, ep.Name), key);
                    return PairResult.Accepted;
                }
                catch (ClipSyncException ex) when (ex.Code is not null and not ClipProtocol.ErrorMismatch)
                {
                    SetError(deviceId, ex.Message);
                    return ex.Code switch
                    {
                        ClipProtocol.ErrorRejected => PairResult.Rejected,
                        ClipProtocol.ErrorTimeout => PairResult.Timeout,
                        ClipProtocol.ErrorBusy => PairResult.Busy,
                        ClipProtocol.ErrorPaused => PairResult.Paused,
                        // 双方同时发起：本机会自动接受对方的请求，以对方的密钥为准
                        ClipProtocol.ErrorConflict => await WaitPairedAsync() ? Accepted() : PairResult.Busy,
                        _ => PairResult.Failed
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return PairResult.Cancelled;
                }
                catch (OperationCanceledException)
                {
                    SetError(deviceId, "对方未响应");
                    return PairResult.Timeout;
                }
                catch
                {
                    // 换下一个地址
                }
            }
            SetError(deviceId, "无法连接到对方");
            return PairResult.Unreachable;
        }
        finally
        {
            lock (_lock)
            {
                if (_pendingPairKeys.TryGetValue(deviceId, out var k) && ReferenceEquals(k, key)) _pendingPairKeys.Remove(deviceId);
            }
            RaiseStateChanged();
        }

        PairResult Accepted()
        {
            ClearError(deviceId);
            return PairResult.Accepted;
        }

        async Task<bool> WaitPairedAsync()
        {
            for (int i = 0; i < 50; i++)
            {
                if (_pairs.Get(deviceId) is { Revoked: false }) return true;
                await Task.Delay(100, CancellationToken.None);
            }
            return false;
        }
    }

    /// <summary>取消与该设备的共享：删除本机配对并尽量通知对方（对方离线时，下次连接会被拒绝）。</summary>
    public void Unpair(string deviceId)
    {
        var pairing = _pairs.Get(deviceId);
        if (pairing is null) return;
        _pairs.Remove(deviceId);
        ClearError(deviceId);
        CloseInbound(deviceId);
        Reconcile();
        if (!pairing.Revoked)
        {
            _ = Task.Run(() => NotifyUnpairAsync(deviceId, pairing.KeyBytes));
        }
    }

    public List<PeerStatus> GetPeers()
    {
        var pairs = _pairs.All().ToDictionary(p => p.DeviceId, StringComparer.Ordinal);
        lock (_lock)
        {
            var ids = new HashSet<string>(_endpoints.Keys, StringComparer.Ordinal);
            ids.UnionWith(pairs.Keys);
            return ids.Select(id =>
            {
                var eps = _endpoints.GetValueOrDefault(id) ?? [];
                pairs.TryGetValue(id, out var pairing);
                return new PeerStatus(
                    id,
                    eps.FirstOrDefault()?.Name ?? pairing?.Name ?? id,
                    eps.ToList(),
                    pairing,
                    _links.TryGetValue(id, out var link) ? link.State : LinkState.Idle,
                    _errors.GetValueOrDefault(id),
                    _inbound.TryGetValue(id, out var sessions) && sessions.Count > 0,
                    _pendingPairKeys.ContainsKey(id));
            }).ToList();
        }
    }

    // ---------------- 发送连接 ----------------

    private void Reconcile()
    {
        var toStop = new List<PeerLink>();
        lock (_lock)
        {
            if (_disposed) return;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            if (_syncEnabled)
            {
                foreach (var p in _pairs.All())
                {
                    if (p.SendEnabled && !p.Revoked && _endpoints.TryGetValue(p.DeviceId, out var eps) && eps.Count > 0)
                        wanted.Add(p.DeviceId);
                }
            }

            foreach (var (id, link) in _links.ToList())
            {
                if (wanted.Contains(id)) continue;
                _links.Remove(id);
                toStop.Add(link);
            }
            foreach (var id in wanted)
            {
                if (_links.ContainsKey(id)) continue;
                var link = new PeerLink(id);
                _links[id] = link;
                link.Task = Task.Run(() => RunLinkAsync(link));
            }
        }
        foreach (var link in toStop) TryCancel(link.Cts);
        RaiseStateChanged();
    }

    private async Task RunLinkAsync(PeerLink link)
    {
        var ct = link.Cts.Token;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            List<PeerEndpoint> endpoints;
            lock (_lock) endpoints = _endpoints.GetValueOrDefault(link.DeviceId)?.ToList() ?? [];

            string? error = "无法连接";
            int retryMs = 0;
            foreach (var ep in endpoints)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    SetLinkState(link, LinkState.Connecting);
                    await RunSessionAsync(link, ep, ct);
                    failures = 0;
                    error = "连接已断开";
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (ClipSyncException ex) when (ex.Code == ClipProtocol.ErrorUnpaired)
                {
                    _pairs.Update(link.DeviceId, r => r with { Revoked = true });
                    SetError(link.DeviceId, ex.Message);
                    Reconcile();
                    return;
                }
                catch (ClipSyncException ex) when (ex.Code is not null and not ClipProtocol.ErrorMismatch)
                {
                    // 对方已应答（暂停、版本不兼容等），不必再试其他地址
                    error = ex.Message;
                    retryMs = 15_000;
                    break;
                }
                catch (ClipSyncException ex)
                {
                    error = ex.Message;
                }
                catch
                {
                    error = "无法连接";
                }
            }

            failures++;
            SetError(link.DeviceId, error);
            SetLinkState(link, LinkState.Retrying);
            if (retryMs == 0) retryMs = failures switch { 1 => 1_000, 2 => 3_000, 3 => 5_000, _ => 10_000 };
            try
            {
                await Task.Delay(retryMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(PeerLink link, PeerEndpoint ep, CancellationToken ct)
    {
        var pairing = _pairs.Get(link.DeviceId) ?? throw new ClipSyncException("未配对");
        using var socket = await ConnectAsync(ep, ct);
        using var reg = ct.Register(() => { try { socket.Dispose(); } catch { } });
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var hs = await HandshakeAsync(stream, link.DeviceId, pairing.KeyBytes, pairKey: null, unpair: false, ct);
        ClearError(link.DeviceId);
        SetLinkState(link, LinkState.Connected);

        // 握手前排队的内容已在历史里，统一由下面的补发处理
        while (link.Queue.Reader.TryRead(out _)) { }

        pairing = _pairs.Get(link.DeviceId);
        if (pairing is null || !pairing.SendEnabled) return;
        var remoteHas = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var item in hs.Reply.Inventory ?? [])
        {
            var t = ClipProtocol.FromUnixMs(item.Time - hs.ServerOffsetMs);
            if (!remoteHas.TryGetValue(item.Id, out var existing) || t > existing) remoteHas[item.Id] = t;
        }
        foreach (var entry in _history.SelectForPeer(_deviceId, pairing.SendEnabledSinceUtc, remoteHas))
        {
            await ClipProtocol.WriteAsync(stream, ClipMessageFor(entry, merge: true), ct);
        }
        await ClipProtocol.WriteAsync(stream, new ClipMessage { Type = ClipProtocol.TypeSyncDone }, ct);

        long lastHeard = Environment.TickCount64;
        var reader = Task.Run(async () =>
        {
            try
            {
                while (await ClipProtocol.ReadAsync(stream, ct) is not null)
                {
                    Volatile.Write(ref lastHeard, Environment.TickCount64);
                }
            }
            catch
            {
                // 连接关闭
            }
        }, CancellationToken.None);

        long lastPing = Environment.TickCount64;
        Task<bool>? queueReady = null;
        while (true)
        {
            queueReady ??= link.Queue.Reader.WaitToReadAsync(ct).AsTask();
            var done = await Task.WhenAny(queueReady, reader, Task.Delay(1_000, ct));
            ct.ThrowIfCancellationRequested();
            if (done == reader) return;

            if (queueReady.IsCompleted)
            {
                queueReady = null;
                while (link.Queue.Reader.TryRead(out var entry))
                {
                    await ClipProtocol.WriteAsync(stream, ClipMessageFor(entry, merge: false), ct);
                }
            }

            long now = Environment.TickCount64;
            if (now - lastPing >= ClipProtocol.PingIntervalMs)
            {
                lastPing = now;
                await ClipProtocol.WriteAsync(stream, new ClipMessage { Type = ClipProtocol.TypePing }, ct);
            }
            if (now - Volatile.Read(ref lastHeard) > ClipProtocol.IdleTimeoutMs)
            {
                throw new ClipSyncException("连接超时");
            }
        }
    }

    private async Task NotifyUnpairAsync(string deviceId, byte[] key)
    {
        List<PeerEndpoint> endpoints;
        lock (_lock) endpoints = _endpoints.GetValueOrDefault(deviceId)?.ToList() ?? [];
        foreach (var ep in endpoints)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(ClipProtocol.HandshakeTimeoutMs);
                using var socket = await ConnectAsync(ep, cts.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                await HandshakeAsync(stream, deviceId, key, pairKey: null, unpair: true, cts.Token);
                return;
            }
            catch
            {
            }
        }
    }

    private sealed record HandshakeResult(ClipMessage Challenge, ClipMessage Reply, long ServerOffsetMs);

    /// <summary>发送方握手：读 challenge，发 hello，等 welcome / ok；对方返回 error 时抛出带错误码的 ClipSyncException。</summary>
    private async Task<HandshakeResult> HandshakeAsync(Stream stream, string expectedId, byte[]? proofKey, byte[]? pairKey, bool unpair, CancellationToken ct)
    {
        using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hsCts.CancelAfter(ClipProtocol.HandshakeTimeoutMs);

        var challenge = await ClipProtocol.ReadAsync(stream, hsCts.Token) ?? throw new ClipSyncException("连接被关闭");
        if (challenge.Type != ClipProtocol.TypeChallenge || challenge.Magic != ClipProtocol.Magic)
            throw new ClipSyncException("对方不是剪贴板共享服务");
        if (challenge.Version != ClipProtocol.Version)
            throw new ClipSyncException("对方插件版本不兼容", ClipProtocol.ErrorVersion);
        if (challenge.DeviceId != expectedId)
            throw new ClipSyncException("该地址已是另一台设备", ClipProtocol.ErrorMismatch);

        long serverOffset = (challenge.Time ?? ClipProtocol.NowMs()) - ClipProtocol.NowMs();
        string nonce = challenge.Nonce ?? string.Empty;
        await ClipProtocol.WriteAsync(stream, new ClipMessage
        {
            Type = ClipProtocol.TypeHello,
            Magic = ClipProtocol.Magic,
            Version = ClipProtocol.Version,
            DeviceId = _deviceId,
            Name = _localName(),
            Time = ClipProtocol.NowMs(),
            Target = expectedId,
            Proof = proofKey is null ? null : ClipProtocol.ComputeProof(proofKey, nonce, _deviceId, expectedId),
            PairKey = pairKey is null ? null : Convert.ToBase64String(pairKey),
            Unpair = unpair ? true : null
        }, hsCts.Token);

        // 首次共享要等对方点“同意”
        if (pairKey is not null) hsCts.CancelAfter(ClipProtocol.ConsentTimeoutMs + 10_000);
        var reply = await ClipProtocol.ReadAsync(stream, hsCts.Token) ?? throw new ClipSyncException("连接被关闭");
        if (reply.Type == ClipProtocol.TypeError)
            throw new ClipSyncException(ErrorText(reply.Error), reply.Error ?? "error");
        if (reply.Type is not (ClipProtocol.TypeWelcome or ClipProtocol.TypeOk))
            throw new ClipSyncException("对方回复无效");
        return new HandshakeResult(challenge, reply, serverOffset);
    }

    private static async Task<Socket> ConnectAsync(PeerEndpoint ep, CancellationToken ct)
    {
        var socket = new Socket(ep.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (ep.LocalAddress is not null)
            {
                // 从对应虚拟网卡发出；绑定失败（地址刚变化）时交给系统选路
                try { socket.Bind(new IPEndPoint(ep.LocalAddress, 0)); } catch (SocketException) { }
            }
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ClipProtocol.ConnectTimeoutMs);
            await socket.ConnectAsync(new IPEndPoint(ep.Address, ep.Port), connectCts.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static ClipMessage ClipMessageFor(ClipEntry entry, bool merge) => new()
    {
        Type = ClipProtocol.TypeClip,
        Merge = merge,
        Clip = ClipProtocol.ToPayload(entry)
    };

    // ---------------- 接收服务 ----------------

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
            _ = Task.Run(() => HandleInboundAsync(socket, ct));
        }
    }

    private async Task HandleInboundAsync(Socket socket, CancellationToken serverCt)
    {
        var remoteIp = Normalize(((IPEndPoint)socket.RemoteEndPoint!).Address);
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        string? sessionDevice = null;
        try
        {
            socket.NoDelay = true;
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            using var reg = connCts.Token.Register(() => { try { socket.Dispose(); } catch { } });

            connCts.CancelAfter(ClipProtocol.HandshakeTimeoutMs);
            string nonce = ClipProtocol.NewNonce();
            await ClipProtocol.WriteAsync(stream, new ClipMessage
            {
                Type = ClipProtocol.TypeChallenge,
                Magic = ClipProtocol.Magic,
                Version = ClipProtocol.Version,
                DeviceId = _deviceId,
                Name = _localName(),
                Nonce = nonce,
                Time = ClipProtocol.NowMs()
            }, connCts.Token);

            var hello = await ClipProtocol.ReadAsync(stream, connCts.Token);
            if (hello is null || hello.Type != ClipProtocol.TypeHello || hello.Magic != ClipProtocol.Magic) return;
            if (hello.Version != ClipProtocol.Version)
            {
                await ReplyErrorAsync(stream, ClipProtocol.ErrorVersion, connCts.Token);
                return;
            }
            if (hello.Target != _deviceId)
            {
                await ReplyErrorAsync(stream, ClipProtocol.ErrorMismatch, connCts.Token);
                return;
            }
            string senderId = hello.DeviceId ?? string.Empty;
            if (!ClipProtocol.IsValidDeviceId(senderId) || senderId == _deviceId) return;
            string name = ClipProtocol.SanitizeName(hello.Name, remoteIp.ToString());
            long offsetMs = (hello.Time ?? ClipProtocol.NowMs()) - ClipProtocol.NowMs();
            bool authed = Authenticate(senderId, name, nonce, hello.Proof);

            if (hello.Unpair == true)
            {
                if (!authed)
                {
                    await ReplyErrorAsync(stream, ClipProtocol.ErrorUnpaired, connCts.Token);
                    return;
                }
                _pairs.Remove(senderId);
                SetError(senderId, "对方已取消共享");
                CloseInbound(senderId);
                Reconcile();
                await ClipProtocol.WriteAsync(stream, new ClipMessage { Type = ClipProtocol.TypeOk }, connCts.Token);
                return;
            }

            if (!SyncEnabled)
            {
                await ReplyErrorAsync(stream, ClipProtocol.ErrorPaused, connCts.Token);
                return;
            }

            // 等待用户同意期间预先读取下一帧：对方断开时立即撤回确认提示；同意后这一帧交给下面的接收循环
            Task<ClipMessage?>? pendingRead = null;
            if (!authed)
            {
                byte[]? key = DecodeKey(hello.PairKey);
                if (key is null)
                {
                    await ReplyErrorAsync(stream, ClipProtocol.ErrorUnpaired, connCts.Token);
                    return;
                }
                pendingRead = ClipProtocol.ReadAsync(stream, connCts.Token);
                string? error = await HandlePairRequestAsync(senderId, name, remoteIp, key, pendingRead, connCts);
                if (error is not null)
                {
                    if (!pendingRead.IsCompleted) await ReplyErrorAsync(stream, error, connCts.Token);
                    return;
                }
                if (pendingRead.IsCompleted) return; // 对方在同意前就断开了，或违反协议提前发送
            }

            sessionDevice = senderId;
            RegisterInbound(senderId, connCts);
            connCts.CancelAfter(ClipProtocol.IdleTimeoutMs);
            await ClipProtocol.WriteAsync(stream, new ClipMessage
            {
                Type = ClipProtocol.TypeWelcome,
                Time = ClipProtocol.NowMs(),
                Inventory = _history.Inventory().Select(kv => new InventoryItem { Id = kv.Key, Time = ClipProtocol.ToUnixMs(kv.Value) }).ToList()
            }, connCts.Token);

            var batch = new List<ClipEntry>();
            try
            {
                while (true)
                {
                    var msg = pendingRead is not null ? await pendingRead : await ClipProtocol.ReadAsync(stream, connCts.Token);
                    pendingRead = null;
                    if (msg is null) break;
                    connCts.CancelAfter(ClipProtocol.IdleTimeoutMs);

                    switch (msg.Type)
                    {
                        case ClipProtocol.TypeClip:
                        {
                            bool merge = msg.Merge == true;
                            var now = DateTime.UtcNow;
                            // 实时内容按收到的时间记录；补发的内容按对方的复制时间（换算成本机时钟）参与排序
                            var time = merge && msg.Clip is not null
                                ? Min(ClipProtocol.FromUnixMs(msg.Clip.Time - offsetMs), now)
                                : now;
                            var entry = ClipProtocol.FromPayload(msg.Clip, time, senderId, name);
                            if (entry is null || !SyncEnabled || IncomingFilter?.Invoke(entry) == false) break;
                            entry = entry with { Merged = merge };
                            if (_history.ApplyRemote(entry) == ApplyOutcome.Ignored) break;
                            if (merge) batch.Add(entry);
                            else RaiseClipsReceived(new ClipsReceivedEventArgs(senderId, name, [entry], false));
                            break;
                        }
                        case ClipProtocol.TypeSyncDone:
                            FlushBatch();
                            break;
                        case ClipProtocol.TypePing:
                            await ClipProtocol.WriteAsync(stream, new ClipMessage { Type = ClipProtocol.TypePong }, connCts.Token);
                            break;
                    }
                }
            }
            finally
            {
                FlushBatch();
            }

            void FlushBatch()
            {
                if (batch.Count == 0) return;
                var applied = batch;
                batch = [];
                RaiseClipsReceived(new ClipsReceivedEventArgs(senderId, name, applied, true));
            }
        }
        catch
        {
            // 对方断开、超时、被关闭或数据错误：结束该连接
        }
        finally
        {
            if (sessionDevice is not null) UnregisterInbound(sessionDevice, connCts);
        }
    }

    private bool Authenticate(string senderId, string name, string nonce, string? proof)
    {
        if (string.IsNullOrEmpty(proof)) return false;
        var pairing = _pairs.Get(senderId);
        if (pairing is not null && ClipProtocol.VerifyProof(pairing.KeyBytes, nonce, senderId, _deviceId, proof))
        {
            // 对方仍持有同一把密钥，说明之前的“已取消”是误判（例如配对时连接竞争）
            if (pairing.Revoked) _pairs.Update(senderId, r => r with { Revoked = false });
            if (pairing.Name != name) _pairs.Update(senderId, r => r with { Name = name });
            return true;
        }

        byte[]? pending;
        lock (_lock) _pendingPairKeys.TryGetValue(senderId, out pending);
        if (pending is not null && ClipProtocol.VerifyProof(pending, nonce, senderId, _deviceId, proof))
        {
            // 本机发起的共享已被对方同意，且对方的发送连接先到
            ConfirmPairing(senderId, name, pending);
            return true;
        }
        return false;
    }

    /// <returns>null 表示同意；否则为回复给对方的错误码。</returns>
    private async Task<string?> HandlePairRequestAsync(string senderId, string name, IPAddress remoteIp, byte[] key, Task pendingRead, CancellationTokenSource connCts)
    {
        bool autoAccept = false;
        lock (_lock)
        {
            if (_pendingPairKeys.ContainsKey(senderId))
            {
                // 双方同时向对方发起共享：设备 ID 较大的一方直接接受对方的请求，另一方回复冲突，最终双方使用同一把密钥
                if (string.CompareOrdinal(_deviceId, senderId) > 0) autoAccept = true;
                else return ClipProtocol.ErrorConflict;
            }
            else if (_consentPending)
            {
                return ClipProtocol.ErrorBusy;
            }
            else
            {
                _consentPending = true;
            }
        }

        if (!autoAccept)
        {
            try
            {
                connCts.CancelAfter(ClipProtocol.ConsentTimeoutMs + 5_000);
                using var askCts = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token);
                askCts.CancelAfter(ClipProtocol.ConsentTimeoutMs);
                _ = pendingRead.ContinueWith(_ => TryCancel(askCts), TaskScheduler.Default);

                bool accepted;
                try
                {
                    accepted = await _consent(new PairRequestInfo(senderId, name, remoteIp), askCts.Token);
                }
                catch (OperationCanceledException)
                {
                    accepted = false;
                }
                if (!accepted) return askCts.IsCancellationRequested ? ClipProtocol.ErrorTimeout : ClipProtocol.ErrorRejected;
                if (pendingRead.IsCompleted) return ClipProtocol.ErrorTimeout;
            }
            finally
            {
                lock (_lock) _consentPending = false;
            }
        }

        ConfirmPairing(senderId, name, key);
        return null;
    }

    private void ConfirmPairing(string deviceId, string name, byte[] key)
    {
        string keyText = Convert.ToBase64String(key);
        var existing = _pairs.Get(deviceId);
        if (existing is { Revoked: false } && existing.Key == keyText)
        {
            if (existing.Name != name) _pairs.Update(deviceId, r => r with { Name = name });
        }
        else
        {
            var now = DateTime.UtcNow;
            _pairs.Upsert(new PairingRecord
            {
                DeviceId = deviceId,
                Name = name,
                Key = keyText,
                SendEnabled = true,
                SendEnabledSinceUtc = now,
                PairedAtUtc = now
            });
        }
        ClearError(deviceId);
        Reconcile();
    }

    private static async Task ReplyErrorAsync(Stream stream, string error, CancellationToken ct)
    {
        try
        {
            await ClipProtocol.WriteAsync(stream, new ClipMessage { Type = ClipProtocol.TypeError, Error = error }, ct);
        }
        catch
        {
        }
    }

    private void RegisterInbound(string deviceId, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (!_inbound.TryGetValue(deviceId, out var set)) _inbound[deviceId] = set = [];
            set.Add(cts);
        }
        RaiseStateChanged();
    }

    private void UnregisterInbound(string deviceId, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (_inbound.TryGetValue(deviceId, out var set))
            {
                set.Remove(cts);
                if (set.Count == 0) _inbound.Remove(deviceId);
            }
        }
        RaiseStateChanged();
    }

    private void CloseInbound(string deviceId)
    {
        List<CancellationTokenSource> sessions;
        lock (_lock) sessions = _inbound.GetValueOrDefault(deviceId)?.ToList() ?? [];
        foreach (var cts in sessions) TryCancel(cts);
    }

    // ---------------- 工具方法 ----------------

    public static string ErrorText(string? code) => code switch
    {
        ClipProtocol.ErrorRejected => "对方拒绝了共享请求",
        ClipProtocol.ErrorTimeout => "对方未在 60 秒内同意",
        ClipProtocol.ErrorBusy => "对方正在处理其他共享请求，请稍后再试",
        ClipProtocol.ErrorConflict => "对方也在请求与你共享",
        ClipProtocol.ErrorUnpaired => "对方已取消共享",
        ClipProtocol.ErrorPaused => "对方已暂停同步",
        ClipProtocol.ErrorMismatch => "该地址已是另一台设备",
        ClipProtocol.ErrorVersion => "对方插件版本不兼容",
        _ => "对方拒绝连接"
    };

    private void SetLinkState(PeerLink link, LinkState state)
    {
        if (link.State == state) return;
        link.State = state;
        RaiseStateChanged();
    }

    private void SetError(string deviceId, string? error)
    {
        lock (_lock)
        {
            if (error is null) _errors.Remove(deviceId);
            else _errors[deviceId] = error;
        }
        RaiseStateChanged();
    }

    private void ClearError(string deviceId) => SetError(deviceId, null);

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    private void RaiseClipsReceived(ClipsReceivedEventArgs args)
    {
        try { ClipsReceived?.Invoke(args); } catch { }
    }

    private static byte[]? DecodeKey(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var key = Convert.FromBase64String(base64);
            return key.Length == ClipProtocol.KeyBytes ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    public async ValueTask DisposeAsync()
    {
        List<PeerLink> links;
        List<CancellationTokenSource> inbound;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            foreach (var l in _listeners.Values)
            {
                try { l.Stop(); } catch { }
            }
            _listeners.Clear();
            links = _links.Values.ToList();
            _links.Clear();
            inbound = _inbound.Values.SelectMany(s => s).ToList();
        }
        foreach (var link in links) TryCancel(link.Cts);
        foreach (var cts in inbound) TryCancel(cts);
        try
        {
            await Task.WhenAll(links.Select(l => l.Task ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
        // 不释放 _cts：仍在收尾的连接会用它创建关联令牌
    }

    private sealed class PeerLink(string deviceId)
    {
        public string DeviceId { get; } = deviceId;
        public Channel<ClipEntry> Queue { get; } = Channel.CreateUnbounded<ClipEntry>();
        public CancellationTokenSource Cts { get; } = new();
        public volatile LinkState State = LinkState.Connecting;
        public Task? Task { get; set; }
    }
}
