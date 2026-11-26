using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using ClipboardShare.Clipboard;
using ClipboardShare.Engine;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;

namespace ClipboardShare;

internal static class Program
{
    private const string MenuId = "main";
    private const int PreviewChars = 300;

    /// <summary>历史卡片左侧色条：按复制后经过的时间分段着色。</summary>
    private static readonly (TimeSpan From, string Color, string Label)[] AgeBuckets =
    [
        (TimeSpan.Zero, "#10B981", "1 分钟内"),
        (TimeSpan.FromMinutes(1), "#3B82F6", "10 分钟内"),
        (TimeSpan.FromMinutes(10), "#8B5CF6", "1 小时内"),
        (TimeSpan.FromHours(1), "#F59E0B", "24 小时内"),
        (TimeSpan.FromHours(24), "#6B7280", "更早")
    ];

    private static readonly int[] TtlOptions = [10, 30, 60, 360, 1440, 0];

    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };
    private static readonly TaskCompletionSource ExitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly object StateLock = new();
    private static readonly SemaphoreSlim HistoryRefreshGate = new(1, 1);
    private static readonly SemaphoreSlim DevicesRefreshGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, string> Thumbnails = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> ThumbnailJobs = new(StringComparer.Ordinal);

    private static ClipSettings _settings = new();
    private static string _dataDir = string.Empty;
    private static string _deviceId = string.Empty;

    private static JsonRpcPipeConnection? _ipc;
    private static ClipSyncNode? _node;
    private static ClipHistory _history = new(TimeSpan.FromHours(1));
    private static ClipboardMonitor? _monitor;
    private static List<HostNetworkStatus> _networks = [];
    private static List<ServicePeerInfo> _servicePeers = [];

    /// <summary>系统剪贴板当前内容的复制时间。插件启动前就在剪贴板里的内容按启动时刻算，重连合并时不会被更早的记录覆盖。</summary>
    private static DateTime _currentClipTimeUtc = DateTime.UtcNow;
    private static string _activity = string.Empty;
    private static PendingConsent? _pendingConsent;
    private static string _lastHistoryJson = string.Empty;
    private static string _lastDevicesJson = string.Empty;
    private static int _historyDirty;
    private static int _devicesDirty;

    public static async Task Main(string[] args)
    {
        string pipeArg = GetArg(args, "--pipe") ?? @"\\.\pipe\easytier_ext_ipc";
        string pluginId = GetArg(args, "--plugin-id") ?? "clipboard-share";
        string token = GetArg(args, "--token") ?? string.Empty;
        _dataDir = GetArg(args, "--data-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Easytier_With_Extension", "plugins_data", pluginId);
        Directory.CreateDirectory(_dataDir);
        _settings = LoadJson<ClipSettings>("settings.json") ?? new ClipSettings();
        _settings.Normalize();
        _deviceId = LoadOrCreateDeviceId();

        string pipeName = pipeArg.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
            ? pipeArg[@"\\.\pipe\".Length..]
            : pipeArg;

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);

        _ipc = new JsonRpcPipeConnection(pipe);
        _ipc.Disconnected += () => ExitTcs.TrySetResult();
        _ipc.Start(requestHandler: null, notificationHandler: HandleHostEventAsync);

        await _ipc.SendRequestAsync<PluginHelloResult>(IpcMethods.PluginHello, new PluginHelloParams
        {
            PluginId = pluginId,
            Token = token
        });

        _networks = await _ipc.SendRequestAsync<List<HostNetworkStatus>>(IpcMethods.HostGetNetworks) ?? [];

        _history = new ClipHistory(_settings.Ttl);
        _history.Changed += ScheduleHistoryRefresh;
        _node = new ClipSyncNode(_deviceId, LocalName, new PairingStore(Path.Combine(_dataDir, "pairs.json")), _history, AskConsentAsync)
        {
            IncomingFilter = e => e.Kind != ClipKind.Image || _settings.SyncImages
        };
        _node.StateChanged += ScheduleDevicesRefresh;
        _node.ClipsReceived += e => _ = SafeAsync(() => OnClipsReceivedAsync(e));
        _node.SetSyncEnabled(_settings.SyncEnabled);
        ApplyBindings();

        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
        {
            MenuId = MenuId,
            Schema = BuildSchema()
        });

        await _ipc.SendRequestAsync<object>(IpcMethods.DiscoveryRegister, new DiscoveryRegisterParams
        {
            Port = ClipProtocol.DefaultPort,
            Proto = "tcp",
            Meta = JsonSerializer.SerializeToElement(new { v = ClipProtocol.Version, id = _deviceId })
        });

        _servicePeers = await _ipc.SendRequestAsync<List<ServicePeerInfo>>(IpcMethods.DiscoveryGetServicePeers) ?? [];
        _node.UpdateEndpoints(BuildEndpoints());

        _monitor = new ClipboardMonitor();
        _monitor.Copied += c => _ = SafeAsync(() => OnLocalCopyAsync(c));
        _monitor.Skipped += SetActivity;
        _monitor.Start();
        if (!_monitor.IsRunning) SetActivity("无法监听系统剪贴板，复制的内容不会同步");

        await RefreshAllAsync();

        // 定时清除过期记录，并在记录跨过时间分段时刷新色条
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _ = Task.Run(async () =>
        {
            try
            {
                while (await timer.WaitForNextTickAsync())
                {
                    _history.Expire();
                    await SafeAsync(RefreshHistoryAsync);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        });

        await ExitTcs.Task;

        timer.Dispose();
        _pendingConsent?.Tcs.TrySetResult(false);
        _monitor.Dispose();
        await _node.DisposeAsync();
        await _ipc.DisposeAsync();
    }

    private static async Task HandleHostEventAsync(string method, JsonElement? @params)
    {
        if (@params is null) return;

        switch (method)
        {
            case IpcMethods.EventNetworksChanged:
            {
                var ev = @params.Value.Deserialize<EventNetworksChangedParams>(IpcJsonContext.Options);
                if (ev is null) break;
                _networks = ev.Networks;
                ApplyBindings();
                _node?.UpdateEndpoints(BuildEndpoints());
                break;
            }
            case IpcMethods.EventDiscoveryUpdated:
            {
                var ev = @params.Value.Deserialize<EventDiscoveryUpdatedParams>(IpcJsonContext.Options);
                if (ev is null) break;
                _servicePeers = ev.ServicePeers;
                _node?.UpdateEndpoints(BuildEndpoints());
                break;
            }
            case IpcMethods.EventUiAction:
            {
                var ev = @params.Value.Deserialize<EventUiActionParams>(IpcJsonContext.Options);
                if (ev is not null && ev.MenuId == MenuId) await HandleUiActionAsync(ev);
                break;
            }
            case IpcMethods.EventShutdown:
                ExitTcs.TrySetResult();
                break;
        }
    }

    private static async Task HandleUiActionAsync(EventUiActionParams ev)
    {
        var node = _node;
        if (node is null) return;

        switch (ev.Key)
        {
            case "devices_table" when !string.IsNullOrEmpty(ev.RowId):
            {
                var peer = node.GetPeers().FirstOrDefault(p => p.DeviceId == ev.RowId);
                if (peer is null) break;
                switch (ev.Action)
                {
                    case "share" when peer.Pairing is { Revoked: false, SendEnabled: false }:
                        node.SetSendEnabled(peer.DeviceId, true);
                        SetActivity($"已恢复向 {peer.Name} 发送，只同步恢复之后复制的内容");
                        break;
                    case "share":
                        StartPair(peer);
                        break;
                    case "pause":
                        node.SetSendEnabled(peer.DeviceId, false);
                        SetActivity($"已暂停向 {peer.Name} 发送，期间复制的内容恢复后也不会补发");
                        break;
                    case "unpair":
                        node.Unpair(peer.DeviceId);
                        SetActivity($"已取消与 {peer.Name} 的共享，再次共享需要对方重新同意");
                        break;
                }
                break;
            }
            case "history" when !string.IsNullOrEmpty(ev.RowId):
                if (ev.Action == "copy") await CopyFromHistoryAsync(ev.RowId);
                else if (ev.Action == "delete") _history.Delete(ev.RowId);
                break;
            case "btn_clear":
                _history.Clear();
                Thumbnails.Clear();
                SetActivity("已清空本机历史（其他设备的历史不受影响）");
                break;
            case "sync_enabled" when ev.Value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v:
                _settings.SyncEnabled = v.GetBoolean();
                SaveJson("settings.json", _settings);
                node.SetSyncEnabled(_settings.SyncEnabled);
                SetActivity(_settings.SyncEnabled ? "已开启同步" : "已关闭同步：复制的内容只记录在本机，重新开启后也不会补发");
                break;
            case "ttl" when ev.Value is { ValueKind: JsonValueKind.Number } v:
                _settings.TtlMinutes = v.GetInt32();
                _settings.Normalize();
                SaveJson("settings.json", _settings);
                _history.Ttl = _settings.Ttl;
                await SafeAsync(RefreshHistoryAsync);
                break;
            case "sync_images" when ev.Value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v:
                _settings.SyncImages = v.GetBoolean();
                SaveJson("settings.json", _settings);
                break;
            case "auto_write" when ev.Value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v:
                _settings.AutoWrite = v.GetBoolean();
                SaveJson("settings.json", _settings);
                break;
            case "btn_accept":
                _pendingConsent?.Tcs.TrySetResult(true);
                break;
            case "btn_reject":
                _pendingConsent?.Tcs.TrySetResult(false);
                break;
        }
    }

    // ---------------- 剪贴板 ----------------

    private static async Task OnLocalCopyAsync(ClipboardContent content)
    {
        var now = DateTime.UtcNow;
        ClipEntry entry;
        if (content.Text is { } text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Encoding.UTF8.GetByteCount(text) > ClipProtocol.MaxClipBytes)
            {
                SetActivity("复制的文字超过 20 MB，未记录");
                return;
            }
            entry = ClipEntry.FromText(text, now, _deviceId, LocalName());
        }
        else
        {
            byte[] png;
            int width, height;
            if (content.Png is { } p)
            {
                png = p;
                (width, height) = ImageCodec.PngSize(p);
            }
            else if (content.Dib is { } dib)
            {
                (png, width, height) = await ImageCodec.DibToPngAsync(dib);
            }
            else
            {
                return;
            }
            if (png.Length > ClipProtocol.MaxClipBytes)
            {
                SetActivity("复制的图片超过 20 MB，未记录");
                return;
            }
            entry = ClipEntry.FromImage(png, width, height, now, _deviceId, LocalName());
        }

        RecordAndPublishLocal(entry, now);
    }

    /// <summary>本机复制（含从历史再次复制）：写入历史并发送给已连接的设备。</summary>
    private static void RecordAndPublishLocal(ClipEntry entry, DateTime nowUtc)
    {
        bool localOnly = !_settings.SyncEnabled || (entry.Kind == ClipKind.Image && !_settings.SyncImages);
        entry = entry with
        {
            TimeUtc = nowUtc,
            OriginDeviceId = _deviceId,
            OriginName = LocalName(),
            Merged = false,
            LocalOnly = localOnly
        };
        lock (StateLock) _currentClipTimeUtc = nowUtc;
        _history.RecordLocal(entry);
        _node?.Publish(entry);

        int connected = _node?.GetPeers().Count(p => p.Link == LinkState.Connected) ?? 0;
        SetActivity(localOnly ? $"已记录{KindText(entry)}（{(_settings.SyncEnabled ? "图片同步已关闭" : "同步已关闭")}，不发送）"
            : connected > 0 ? $"已将{KindText(entry)}发送到 {connected} 台设备"
            : $"已记录{KindText(entry)}，设备连接后自动补发");
    }

    private static async Task OnClipsReceivedAsync(ClipsReceivedEventArgs e)
    {
        var newest = e.Entries.MaxBy(x => x.TimeUtc)!;
        SetActivity(e.Merge
            ? $"已与 {e.Name} 合并 {e.Entries.Count} 条记录"
            : $"收到 {e.Name} 复制的{KindText(newest)}");

        if (!_settings.AutoWrite) return;
        lock (StateLock)
        {
            // 合并来的记录只有比本机剪贴板当前内容更新时才写入
            if (e.Merge && newest.TimeUtc <= _currentClipTimeUtc) return;
            _currentClipTimeUtc = newest.TimeUtc;
        }
        try
        {
            await WriteClipboardAsync(newest);
        }
        catch (Exception ex)
        {
            SetActivity($"写入剪贴板失败：{ex.Message}");
        }
    }

    private static async Task CopyFromHistoryAsync(string id)
    {
        var entry = _history.Get(id);
        if (entry is null) return;
        try
        {
            await WriteClipboardAsync(entry);
        }
        catch (Exception ex)
        {
            SetActivity($"写入剪贴板失败：{ex.Message}");
            return;
        }
        // 视为一次新的复制：移到最前并同步给其他设备
        RecordAndPublishLocal(entry, DateTime.UtcNow);
    }

    private static async Task WriteClipboardAsync(ClipEntry entry)
    {
        if (_monitor is null) return;
        if (entry.Kind == ClipKind.Text)
        {
            await _monitor.WriteTextAsync(entry.Text ?? string.Empty);
        }
        else if (entry.Png is { } png)
        {
            var dib = await ImageCodec.PngToDibAsync(png);
            await _monitor.WriteImageAsync(png, dib);
        }
    }

    // ---------------- 配对 ----------------

    private static void StartPair(PeerStatus peer)
    {
        if (!peer.Online || peer.PairingInProgress) return;
        _ = Task.Run(async () =>
        {
            SetActivity($"已向 {peer.Name} 发送共享请求，等待对方同意（最多 60 秒）");
            var result = await _node!.RequestPairAsync(peer.DeviceId);
            SetActivity(result switch
            {
                PairResult.Accepted => $"{peer.Name} 已同意，开始同步剪贴板",
                PairResult.Rejected => $"{peer.Name} 拒绝了共享请求",
                PairResult.Timeout => $"{peer.Name} 未在 60 秒内同意",
                PairResult.Busy => $"{peer.Name} 正在处理其他共享请求，请稍后再试",
                PairResult.Paused => $"{peer.Name} 已关闭同步，暂时无法共享",
                PairResult.Unreachable => $"无法连接到 {peer.Name}，请确认对方在线并允许插件通过防火墙",
                _ => $"向 {peer.Name} 发起共享失败"
            });
        });
    }

    private static async Task<bool> AskConsentAsync(PairRequestInfo request, CancellationToken ct)
    {
        var pending = new PendingConsent(request, DateTime.UtcNow.AddMilliseconds(ClipProtocol.ConsentTimeoutMs));
        lock (StateLock)
        {
            if (_pendingConsent is not null) return false;
            _pendingConsent = pending;
        }

        using var reg = ct.Register(() => pending.Tcs.TrySetResult(false));
        try
        {
            await SafeAsync(RebuildSchemaAsync);
            await SafeAsync(() => _ipc!.SendRequestAsync<object>(IpcMethods.UiNavigateTo, new UiNavigateToParams { MenuId = MenuId }));
            _ = CountdownAsync(pending);
            bool accepted = await pending.Tcs.Task;
            SetActivity(accepted
                ? $"已同意与 {request.Name} 共享剪贴板"
                : ct.IsCancellationRequested ? $"{request.Name} 的共享请求已超时" : $"已拒绝 {request.Name} 的共享请求");
            return accepted;
        }
        finally
        {
            lock (StateLock) _pendingConsent = null;
            await SafeAsync(RebuildSchemaAsync);
        }
    }

    private static async Task CountdownAsync(PendingConsent pending)
    {
        while (!pending.Tcs.Task.IsCompleted)
        {
            await SafeAsync(() => UpdateValuesAsync(new() { ["consent_status"] = ConsentText(pending) }));
            await Task.WhenAny(pending.Tcs.Task, Task.Delay(1000));
        }
    }

    private static string ConsentText(PendingConsent p)
    {
        int left = Math.Max(0, (int)Math.Ceiling((p.Deadline - DateTime.UtcNow).TotalSeconds));
        return $"{p.Request.Name}（{p.Request.RemoteIp}）请求与你共享剪贴板：同意后双方复制的文字和图片会互相同步 · {left} 秒后自动拒绝";
    }

    // ---------------- 网络与设备 ----------------

    private static void ApplyBindings()
    {
        _node?.SetBindAddresses(_networks.Where(n => n.Running)
            .Select(n => IPAddress.TryParse(StripPrefix(n.Ipv4), out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToList());
    }

    private static List<PeerEndpoint> BuildEndpoints()
    {
        var networks = _networks;
        return _servicePeers
            .Select(p => (Peer: p, Id: PeerDeviceId(p)))
            .Where(x => x.Id is not null && x.Id != _deviceId && PeerVersion(x.Peer) == ClipProtocol.Version && IPAddress.TryParse(x.Peer.Ipv4, out _))
            .OrderBy(x => string.Equals(x.Peer.RouteType, "p2p", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => ParseLatency(x.Peer.LatencyMs))
            .Select(x =>
            {
                var net = networks.FirstOrDefault(n => n.NetworkId == x.Peer.NetworkId);
                IPAddress? local = IPAddress.TryParse(StripPrefix(net?.Ipv4), out var addr) ? addr : null;
                string name = string.IsNullOrWhiteSpace(x.Peer.Hostname) ? x.Peer.Ipv4 : x.Peer.Hostname;
                return new PeerEndpoint(x.Id!, name, IPAddress.Parse(x.Peer.Ipv4), x.Peer.Port > 0 ? x.Peer.Port : ClipProtocol.DefaultPort, local, x.Peer.NetworkName);
            })
            .ToList();
    }

    private static string? PeerDeviceId(ServicePeerInfo p) =>
        p.Meta.ValueKind == JsonValueKind.Object && p.Meta.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
        ClipProtocol.IsValidDeviceId(id.GetString())
            ? id.GetString()
            : null;

    private static int PeerVersion(ServicePeerInfo p) =>
        p.Meta.ValueKind == JsonValueKind.Object && p.Meta.TryGetProperty("v", out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static string LocalName()
    {
        var name = _networks.FirstOrDefault(n => n.Running && !string.IsNullOrWhiteSpace(n.Hostname))?.Hostname;
        return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;
    }

    private static string StateText(PeerStatus p, bool syncEnabled)
    {
        if (p.PairingInProgress) return "等待对方同意…";
        var pairing = p.Pairing;
        if (pairing is null)
        {
            if (!p.Online) return "离线";
            return p.Error is { } e ? $"未共享（{e}）" : "未共享";
        }
        if (pairing.Revoked) return "对方已取消共享";
        if (!p.Online) return "离线 · 上线后自动合并";
        if (!syncEnabled) return "本机已关闭同步";

        string send = !pairing.SendEnabled
            ? "已暂停发送"
            : p.Link switch
            {
                LinkState.Connected => "发送中",
                LinkState.Retrying => p.Error is { } err ? $"{err}，正在重连" : "正在重连",
                _ => "连接中…"
            };
        if (pairing.SendEnabled && p.Link == LinkState.Connected && p.Receiving) return "双向同步";
        return $"{send} · {(p.Receiving ? "接收中" : "对方未发送")}";
    }

    // ---------------- 界面 ----------------

    private static async Task RefreshAllAsync()
    {
        await RefreshDevicesAsync();
        await RefreshHistoryAsync();
    }

    private static async Task RebuildSchemaAsync()
    {
        if (_ipc is null) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
        {
            MenuId = MenuId,
            Schema = BuildSchema()
        });
        _lastDevicesJson = _lastHistoryJson = string.Empty;
        await RefreshAllAsync();
    }

    private static void ScheduleDevicesRefresh()
    {
        if (Interlocked.Exchange(ref _devicesDirty, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            Interlocked.Exchange(ref _devicesDirty, 0);
            await SafeAsync(RefreshDevicesAsync);
        });
    }

    private static void ScheduleHistoryRefresh()
    {
        if (Interlocked.Exchange(ref _historyDirty, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(120);
            Interlocked.Exchange(ref _historyDirty, 0);
            await SafeAsync(RefreshHistoryAsync);
        });
    }

    private static void SetActivity(string text)
    {
        _activity = $"{DateTime.Now:HH:mm:ss} {text}";
        ScheduleDevicesRefresh();
    }

    private static async Task RefreshDevicesAsync()
    {
        var node = _node;
        if (node is null) return;
        await DevicesRefreshGate.WaitAsync();
        try
        {
            var values = new Dictionary<string, object?>
            {
                ["devices_table"] = BuildDeviceRows(node),
                ["status"] = BuildStatusText(node),
                ["activity"] = string.IsNullOrEmpty(_activity) ? "复制文字或图片后会立即发送给已共享的设备" : _activity
            };
            string json = JsonSerializer.Serialize(values);
            if (json == _lastDevicesJson) return;
            await UpdateValuesAsync(values);
            _lastDevicesJson = json;
        }
        finally
        {
            DevicesRefreshGate.Release();
        }
    }

    private static string BuildStatusText(ClipSyncNode node)
    {
        var peers = node.GetPeers();
        int shared = peers.Count(p => p.Pairing is { Revoked: false });
        int syncing = peers.Count(p => p.Link == LinkState.Connected || p.Receiving);
        var parts = new List<string> { LocalName() };
        if (!node.SyncEnabled) parts.Add("同步已关闭");
        else if (node.LastBindError is { } err) parts.Add($"接收服务异常：{err}");
        else if (node.ListeningCount == 0) parts.Add("暂无在线虚拟网络");
        parts.Add(shared == 0 ? "尚未与任何设备共享" : $"已共享 {shared} 台 · 正在同步 {syncing} 台");
        return string.Join(" · ", parts);
    }

    private static List<Dictionary<string, object?>> BuildDeviceRows(ClipSyncNode node)
    {
        bool syncEnabled = node.SyncEnabled;
        var rows = node.GetPeers()
            .OrderBy(p => p.Online ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .Select(p =>
            {
                var pairing = p.Pairing;
                bool canShare = p.Online && !p.PairingInProgress && syncEnabled &&
                                (pairing is null || pairing.Revoked || !pairing.SendEnabled);
                return new Dictionary<string, object?>
                {
                    ["row_id"] = p.DeviceId,
                    ["name"] = p.Name,
                    ["ipv4"] = p.Endpoints.FirstOrDefault()?.Address.ToString() ?? "-",
                    ["network"] = p.Endpoints.Count == 0 ? "-" : string.Join(" / ", p.Endpoints.Select(e => e.NetworkName).Distinct()),
                    ["state"] = StateText(p, syncEnabled),
                    ["can_share"] = canShare,
                    ["can_pause"] = pairing is { Revoked: false, SendEnabled: true },
                    ["can_unpair"] = pairing is not null && !p.PairingInProgress
                };
            })
            .ToList();

        // 组网内运行旧版或不兼容版本的节点也列出来，便于排查
        foreach (var p in _servicePeers.Where(p => PeerDeviceId(p) is null || PeerVersion(p) != ClipProtocol.Version))
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["row_id"] = $"x|{p.NetworkId}|{p.Ipv4}",
                ["name"] = string.IsNullOrWhiteSpace(p.Hostname) ? p.Ipv4 : p.Hostname,
                ["ipv4"] = p.Ipv4,
                ["network"] = p.NetworkName,
                ["state"] = "插件版本不兼容"
            });
        }
        return rows;
    }

    private static async Task RefreshHistoryAsync()
    {
        await HistoryRefreshGate.WaitAsync();
        try
        {
            var values = new Dictionary<string, object?>
            {
                ["history"] = BuildHistoryValue()
            };
            string json = JsonSerializer.Serialize(values);
            if (json == _lastHistoryJson) return;
            await UpdateValuesAsync(values);
            _lastHistoryJson = json;
        }
        finally
        {
            HistoryRefreshGate.Release();
        }
    }

    private static object BuildHistoryValue()
    {
        var now = DateTime.UtcNow;
        var entries = _history.Snapshot();
        var ids = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in Thumbnails.Keys.Where(k => !ids.Contains(k)).ToList()) Thumbnails.TryRemove(stale, out _);

        var cards = entries.Select(e =>
        {
            var card = new Dictionary<string, object?>
            {
                ["id"] = e.Id,
                ["accent"] = BucketFor(now - e.TimeUtc).Color
            };
            if (e.Kind == ClipKind.Text)
            {
                card["text"] = Preview(e.Text);
            }
            else if (Thumbnails.TryGetValue(e.Id, out var thumb))
            {
                if (thumb.Length > 0) card["image_base64"] = thumb;
            }
            else
            {
                QueueThumbnail(e);
            }
            return card;
        }).ToList();

        var ttl = _settings.Ttl;
        var legend = AgeBuckets
            .Where(b => ttl is null || b.From < ttl.Value)
            .Select(b => new { label = b.Label, color = b.Color })
            .ToList();
        return new { legend, cards };
    }

    private static (TimeSpan From, string Color, string Label) BucketFor(TimeSpan age) =>
        AgeBuckets.Last(b => age >= b.From);

    private static void QueueThumbnail(ClipEntry e)
    {
        if (e.Png is not { } png || !ThumbnailJobs.TryAdd(e.Id, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                Thumbnails[e.Id] = await ImageCodec.ThumbnailBase64Async(png);
            }
            catch
            {
                Thumbnails[e.Id] = string.Empty;
            }
            finally
            {
                ThumbnailJobs.TryRemove(e.Id, out _);
            }
            ScheduleHistoryRefresh();
        });
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string s = text.Length > PreviewChars * 2 ? text[..(PreviewChars * 2)] : text;
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return s.Length > PreviewChars ? s[..PreviewChars] + "…" : s;
    }

    private static string KindText(ClipEntry e) => e.Kind == ClipKind.Text ? "文字" : "图片";

    private static string TtlText(int minutes) => minutes switch
    {
        < 60 => $"{minutes} 分钟",
        _ => $"{minutes / 60} 小时"
    };

    private static async Task UpdateValuesAsync(Dictionary<string, object?> values)
    {
        if (_ipc is null) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.UiUpdateValues, new UiUpdateValuesParams
        {
            MenuId = MenuId,
            Values = values.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value))
        });
    }

    private static UiPageSchema BuildSchema()
    {
        var sections = new List<UiSection>();

        if (_pendingConsent is { } pending)
        {
            sections.Add(new UiSection
            {
                Id = "consent",
                Title = "共享请求",
                Compact = true,
                Items =
                [
                    new UiItem { Key = "consent_status", Type = "status", Label = "请求", Value = J(ConsentText(pending)) },
                    new UiItem { Key = "btn_reject", Type = "button", Label = "拒绝", Danger = true },
                    new UiItem { Key = "btn_accept", Type = "button", Label = "同意" }
                ]
            });
        }

        sections.Add(new UiSection
        {
            Id = "devices",
            Title = "共享设备",
            Items =
            [
                new UiItem { Key = "status", Type = "status", Label = "本机", Value = J(string.Empty) },
                new UiItem
                {
                    Key = "devices_table",
                    Type = "table",
                    Placeholder = "暂无发现组网内运行剪贴板共享的设备（双方都需要安装并启用本插件）",
                    Columns =
                    [
                        new() { Key = "name", Label = "设备" },
                        new() { Key = "ipv4", Label = "虚拟 IP" },
                        new() { Key = "network", Label = "所属网络" },
                        new() { Key = "state", Label = "状态" }
                    ],
                    RowActions =
                    [
                        new() { Id = "share", Label = "共享", WhenKey = "can_share", WhenEquals = J(true) },
                        new() { Id = "pause", Label = "暂停", WhenKey = "can_pause", WhenEquals = J(true) },
                        new() { Id = "unpair", Label = "取消共享", WhenKey = "can_unpair", WhenEquals = J(true) }
                    ],
                    Rows = []
                },
                new UiItem { Key = "activity", Type = "status", Label = "最近动态", Value = J(string.Empty) }
            ]
        });

        sections.Add(new UiSection
        {
            Id = "settings",
            Title = "同步设置",
            Items =
            [
                new UiItem
                {
                    Key = "sync_enabled", Type = "switch", Label = "同步剪贴板",
                    Subtitle = "关闭后不发送也不接收，期间复制的内容只记录在本机，重新开启后也不会补发",
                    Value = J(_settings.SyncEnabled)
                },
                new UiItem
                {
                    Key = "ttl", Type = "select", Label = "自动清除",
                    Subtitle = "按复制时间计算，不限条数；历史不写入磁盘",
                    Value = J(_settings.TtlMinutes),
                    Options = TtlOptions.Select(m => Option(m == 0 ? "从不清除" : $"复制 {TtlText(m)}后", m)).ToList()
                },
                new UiItem
                {
                    Key = "sync_images", Type = "switch", Label = "同步图片",
                    Subtitle = "单张图片不超过 20 MB；关闭后图片只记录在本机，也不接收其他设备的图片",
                    Value = J(_settings.SyncImages)
                },
                new UiItem
                {
                    Key = "auto_write", Type = "switch", Label = "写入剪贴板",
                    Subtitle = "收到其他设备复制的内容后直接替换本机剪贴板；关闭后只加入历史，需要时点“复制”",
                    Value = J(_settings.AutoWrite)
                }
            ]
        });

        sections.Add(new UiSection
        {
            Id = "history",
            Title = "剪贴板历史",
            Items =
            [
                new UiItem
                {
                    Key = "history",
                    Type = "card_list",
                    Placeholder = "暂无记录，复制文字或图片后会显示在这里",
                    RowActions =
                    [
                        new() { Id = "copy", Label = "复制" },
                        new() { Id = "delete", Label = "删除" }
                    ],
                    Value = J(new { legend = Array.Empty<object>(), cards = Array.Empty<object>() })
                },
                new UiItem { Key = "btn_clear", Type = "button", Label = "清空历史" }
            ]
        });

        return new UiPageSchema { Sections = sections };
    }

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static UiSelectOption Option<T>(string label, T value) => new() { Label = label, Value = J(value) };

    // ---------------- 工具方法 ----------------

    private static string LoadOrCreateDeviceId()
    {
        var identity = LoadJson<DeviceIdentity>("identity.json");
        if (identity is not null && ClipProtocol.IsValidDeviceId(identity.DeviceId)) return identity.DeviceId;
        identity = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N") };
        SaveJson("identity.json", identity);
        return identity.DeviceId;
    }

    private static string? StripPrefix(string? ip) => ip?.Split('/')[0].Trim();

    private static double ParseLatency(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : double.MaxValue;

    private static async Task SafeAsync(Func<Task> action)
    {
        try { await action(); } catch { /* 宿主已断开或正在退出 */ }
    }

    private static T? LoadJson<T>(string name) where T : class
    {
        try
        {
            var path = Path.Combine(_dataDir, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveJson<T>(string name, T value)
    {
        try
        {
            File.WriteAllText(Path.Combine(_dataDir, name), JsonSerializer.Serialize(value, FileJsonOptions));
        }
        catch
        {
        }
    }

    private static string? GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == key) return args[i + 1];
        }
        return null;
    }

    private sealed record PendingConsent(PairRequestInfo Request, DateTime Deadline)
    {
        public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DeviceIdentity
    {
        public string DeviceId { get; set; } = string.Empty;
    }

    private sealed class ClipSettings
    {
        public bool SyncEnabled { get; set; } = true;
        /// <summary>复制后多少分钟清除，0 表示不清除。</summary>
        public int TtlMinutes { get; set; } = 60;
        public bool SyncImages { get; set; } = true;
        public bool AutoWrite { get; set; } = true;

        public TimeSpan? Ttl => TtlMinutes > 0 ? TimeSpan.FromMinutes(TtlMinutes) : null;

        public void Normalize()
        {
            if (!TtlOptions.Contains(TtlMinutes)) TtlMinutes = 60;
        }
    }
}
