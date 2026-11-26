using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using MouseShare.Engine;
using MouseShare.Native;

namespace MouseShare;

internal static class Program
{
    private const string MainMenuId = "main";
    private const int MaintenanceIntervalMs = 2000;
    /// <summary>id 较大的一方等对方先连；对方迟迟没连上时再主动连接（例如对方的入站连接被防火墙拦截）。</summary>
    private const int FallbackDialDelayMs = 6000;

    private static readonly int[] ScalePercents = [100, 125, 150, 175, 200, 225, 250, 300];
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };
    private static readonly TaskCompletionSource ExitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly object StateLock = new();
    private static readonly object ConnectLock = new();

    private static string _dataDir = string.Empty;
    private static ShareSettings _settings = new();
    private static List<PairedPeer> _peers = [];
    private static LayoutDoc _doc = new();

    private static JsonRpcPipeConnection? _ipc;
    private static PeerHub? _hub;
    private static InputEngine? _engine;
    private static List<HostNetworkStatus> _networks = [];
    private static List<ServicePeerInfo> _servicePeers = [];
    private static readonly HashSet<string> Pairing = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> OnlineSince = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> DialAttempts = new(StringComparer.Ordinal);

    private static string? _selected;
    private static bool _sizeFieldShown;
    private static PendingConsent? _pendingConsent;
    private static (string Text, long Until)? _notice;
    private static bool _hooksWanted;

    private static int _dirty;
    private const int DirtyStatus = 1, DirtyLayout = 2, DirtyDevices = 4, DirtyMonitor = 8;

    private static string LocalId => _settings.DeviceId;

    public static async Task Main(string[] args)
    {
        // 物理像素坐标：钩子、SetCursorPos 与显示器枚举都依赖 Per-Monitor V2
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        string pipeArg = GetArg(args, "--pipe") ?? @"\\.\pipe\easytier_ext_ipc";
        string pluginId = GetArg(args, "--plugin-id") ?? "mouse-share";
        string token = GetArg(args, "--token") ?? string.Empty;
        _dataDir = GetArg(args, "--data-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Easytier_With_Extension", "plugins_data", pluginId);
        Directory.CreateDirectory(_dataDir);
        LoadState();

        string pipeName = pipeArg.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
            ? pipeArg[@"\\.\pipe\".Length..]
            : pipeArg;

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);

        _ipc = new JsonRpcPipeConnection(pipe);
        _ipc.Disconnected += () => ExitTcs.TrySetResult();
        _ipc.Start(requestHandler: null, notificationHandler: HandleHostEventAsync);

        await _ipc.SendRequestAsync<PluginHelloResult>(IpcMethods.PluginHello, new PluginHelloParams { PluginId = pluginId, Token = token });
        _networks = await _ipc.SendRequestAsync<List<HostNetworkStatus>>(IpcMethods.HostGetNetworks) ?? [];
        RefreshLocalMonitors();

        _hub = new PeerHub(LocalId, LocalName, GetKey)
        {
            ConsentAsync = AskConsentAsync,
            Paired = OnPairedByRemote,
            RemoteUnpaired = id => RemovePeer(id, notify: false)
        };
        _hub.SessionOpened += OnSessionOpened;
        _hub.SessionClosed += OnSessionClosed;
        _hub.MessageReceived += OnPeerMessage;
        ApplyBindings();

        _engine = new InputEngine((id, frame) => _hub.Send(id, frame));
        _engine.StateChanged += () => MarkDirty(DirtyStatus | DirtyLayout);
        _engine.SetEnabled(_settings.Enabled);
        RebuildSnapshot();

        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams { MenuId = MainMenuId, Schema = BuildSchema() });
        await _ipc.SendRequestAsync<object>(IpcMethods.DiscoveryRegister, new DiscoveryRegisterParams
        {
            Port = Protocol.DefaultPort,
            Proto = "tcp",
            Meta = JsonSerializer.SerializeToElement(new { v = Protocol.Version, id = LocalId })
        });
        _servicePeers = await _ipc.SendRequestAsync<List<ServicePeerInfo>>(IpcMethods.DiscoveryGetServicePeers) ?? [];

        MarkDirty(DirtyStatus | DirtyLayout | DirtyDevices | DirtyMonitor);
        _ = Task.Run(UiLoopAsync);
        _ = Task.Run(MaintenanceLoopAsync);

        await ExitTcs.Task;

        _pendingConsent?.Tcs.TrySetResult(false);
        _engine.Dispose();
        await _hub.DisposeAsync();
        await _ipc.DisposeAsync();
    }

    // ---------------- 宿主事件 ----------------

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
                MarkDirty(DirtyStatus);
                break;
            }
            case IpcMethods.EventDiscoveryUpdated:
            {
                var ev = @params.Value.Deserialize<EventDiscoveryUpdatedParams>(IpcJsonContext.Options);
                if (ev is null) break;
                _servicePeers = ev.ServicePeers;
                ConnectPeers();
                MarkDirty(DirtyDevices | DirtyStatus);
                break;
            }
            case IpcMethods.EventUiAction:
            {
                var ev = @params.Value.Deserialize<EventUiActionParams>(IpcJsonContext.Options);
                if (ev is not null && ev.MenuId == MainMenuId) await HandleUiActionAsync(ev);
                break;
            }
            case IpcMethods.EventShutdown:
                ExitTcs.TrySetResult();
                break;
        }
    }

    private static async Task HandleUiActionAsync(EventUiActionParams ev)
    {
        switch (ev.Key)
        {
            case "layout" when ev.Action == "select" && ev.Value is { ValueKind: JsonValueKind.String } v:
                _selected = v.GetString();
                await AfterSelectionChangedAsync();
                break;
            case "layout" when ev.Action == "move" && ev.Value is { ValueKind: JsonValueKind.Object } v:
                MoveDevice(v);
                break;
            case "mon_scale" when ev.Value is { ValueKind: JsonValueKind.String } v:
                SetScale(v.GetString() ?? MonitorSetting.ModeAuto);
                await AfterSelectionChangedAsync();
                break;
            case "mon_inches" when ev.Value is { ValueKind: JsonValueKind.Number } v:
                SetInches(v.GetDouble());
                break;
            case "devices" when ev.Action == "add" && !string.IsNullOrEmpty(ev.RowId):
                StartPairing(ev.RowId);
                break;
            case "devices" when ev.Action == "remove" && !string.IsNullOrEmpty(ev.RowId):
                RemovePeer(ev.RowId, notify: true);
                break;
            case "btn_identify":
                Identify();
                break;
            case "btn_toggle":
                lock (StateLock) _settings.Enabled = !_settings.Enabled;
                SaveJson("settings.json", _settings);
                _engine?.SetEnabled(_settings.Enabled);
                UpdateHooks();
                MarkDirty(DirtyStatus | DirtyLayout);
                break;
            case "btn_accept":
                _pendingConsent?.Tcs.TrySetResult(true);
                break;
            case "btn_reject":
                _pendingConsent?.Tcs.TrySetResult(false);
                break;
        }
    }

    // ---------------- 配对 ----------------

    private static void StartPairing(string deviceId)
    {
        var target = FindServicePeer(deviceId);
        if (target is null || !IPAddress.TryParse(target.Ipv4, out var ip)) return;
        lock (StateLock)
        {
            if (_peers.Any(p => p.Id == deviceId) || !Pairing.Add(deviceId)) return;
        }
        MarkDirty(DirtyDevices | DirtyStatus);

        var key = RandomNumberGenerator.GetBytes(Protocol.KeyBytes);
        var endpoint = new IPEndPoint(ip, target.Port > 0 ? target.Port : Protocol.DefaultPort);
        _ = Task.Run(async () =>
        {
            var (outcome, name) = await _hub!.RequestPairAsync(deviceId, endpoint, key, CancellationToken.None);
            string displayName = string.IsNullOrWhiteSpace(name) ? target.Hostname : name;
            lock (StateLock) Pairing.Remove(deviceId);
            switch (outcome)
            {
                case PairOutcome.Accepted:
                    AddPeer(deviceId, displayName, key, needsPlacement: true);
                    _hub.Connect(deviceId, endpoint);
                    break;
                case PairOutcome.Rejected:
                    SetNotice($"{displayName} 拒绝了添加请求");
                    break;
                default:
                    SetNotice($"无法连接 {displayName}");
                    break;
            }
            MarkDirty(DirtyDevices | DirtyStatus | DirtyLayout);
        });
    }

    private static async Task<bool> AskConsentAsync(PairRequestInfo request, CancellationToken ct)
    {
        var pending = new PendingConsent(request, DateTime.UtcNow.AddMilliseconds(Protocol.ConsentTimeoutMs));
        lock (StateLock)
        {
            if (_pendingConsent is not null) return false;
            _pendingConsent = pending;
        }

        using var reg = ct.Register(() => pending.Tcs.TrySetResult(false));
        try
        {
            await SafeAsync(RebuildSchemaAsync);
            await SafeAsync(() => _ipc!.SendRequestAsync<object>(IpcMethods.UiNavigateTo, new UiNavigateToParams { MenuId = MainMenuId }));
            _ = CountdownAsync(pending);
            return await pending.Tcs.Task;
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
        return $"{p.Request.Name}（{p.Request.RemoteIp}）请求共享键盘和鼠标 · {left} 秒后自动拒绝";
    }

    private static void OnPairedByRemote(string id, string name, byte[] key) =>
        AddPeer(id, string.IsNullOrWhiteSpace(name) ? "设备" : name.Trim(), key, needsPlacement: false);

    private static void AddPeer(string id, string name, byte[] key, bool needsPlacement)
    {
        lock (StateLock)
        {
            _peers.RemoveAll(p => p.Id == id);
            _peers.Add(new PairedPeer { Id = id, Name = name, Key = Convert.ToBase64String(key), NeedsPlacement = needsPlacement });
        }
        SaveJson("peers.json", _peers);
        MarkDirty(DirtyDevices | DirtyStatus | DirtyLayout);
    }

    private static void RemovePeer(string id, bool notify)
    {
        bool removed;
        lock (StateLock)
        {
            removed = _peers.RemoveAll(p => p.Id == id) > 0;
            _doc.Devices.Remove(id);
            if (_selected?.StartsWith(id + "|", StringComparison.Ordinal) == true) _selected = null;
        }
        if (!removed) return;
        SaveJson("peers.json", _peers);
        SaveDoc();

        if (notify && _hub!.Send(id, Protocol.Frame(MsgType.Unpair)))
        {
            // 留一点时间把通知发出去再断开
            _ = Task.Delay(500).ContinueWith(_ => _hub.Disconnect(id));
        }
        else
        {
            _hub!.Disconnect(id);
        }
        _engine?.PeerLost(id);
        RebuildSnapshot();
        UpdateHooks();
        MarkDirty(DirtyDevices | DirtyStatus | DirtyLayout | DirtyMonitor);
    }

    private static byte[]? GetKey(string id)
    {
        lock (StateLock)
        {
            var p = _peers.FirstOrDefault(x => x.Id == id);
            if (p is null) return null;
            try { return Convert.FromBase64String(p.Key); }
            catch (FormatException) { return null; }
        }
    }

    // ---------------- 连接 ----------------

    private static void OnSessionOpened(PeerSession session)
    {
        session.Send(DocFrame());
        lock (ConnectLock) DialAttempts.Remove(session.PeerId);
        RebuildSnapshot();
        UpdateHooks();
        MarkDirty(DirtyDevices | DirtyStatus | DirtyLayout);
    }

    private static void OnSessionClosed(PeerSession session)
    {
        _engine?.PeerLost(session.PeerId);
        RebuildSnapshot();
        UpdateHooks();
        MarkDirty(DirtyDevices | DirtyStatus | DirtyLayout);
    }

    private static void OnPeerMessage(PeerSession session, MsgType type, byte[] payload)
    {
        switch (type)
        {
            case MsgType.Layout:
                OnLayoutReceived(session.PeerId, payload);
                break;
            case MsgType.Unpair:
                RemovePeer(session.PeerId, notify: false);
                break;
            case MsgType.Identify:
                if (Protocol.ParseJson<IdentifyMsg>(payload) is { } msg) _engine?.ShowIdentify(msg.Numbers);
                break;
            default:
                _engine?.HandleRemote(session.PeerId, type, payload);
                break;
        }
    }

    private static void OnLayoutReceived(string from, byte[] payload)
    {
        var incoming = Protocol.ParseJson<LayoutDoc>(payload);
        if (incoming is null) return;

        bool changed, placed = false;
        lock (StateLock)
        {
            var known = KnownIds().ToHashSet(StringComparer.Ordinal);
            // 本机显示器只以自己枚举的为准
            incoming.Devices.Remove(LocalId, out var mine);
            changed = _doc.Merge(incoming, known);
            if (mine is not null && _doc.Devices.TryGetValue(LocalId, out var self) && mine.Rev > self.Rev)
            {
                self.X = mine.X;
                self.Y = mine.Y;
                self.Rev = mine.Rev;
                self.Settings = mine.Clone().Settings;
                changed = true;
            }

            // 本机发起配对的新设备：收到它的显示器后放到本机右侧
            var peer = _peers.FirstOrDefault(p => p.Id == from);
            if (peer is { NeedsPlacement: true } && _doc.Devices.TryGetValue(from, out var entry) && entry.Monitors.Count > 0)
            {
                var (x, y) = Geometry.PlaceToRight(_doc, KnownIds(), from, LocalId);
                entry.X = x;
                entry.Y = y;
                entry.Rev = _doc.NextRev();
                peer.NeedsPlacement = false;
                placed = changed = true;
            }
        }

        if (placed) SaveJson("peers.json", _peers);
        if (!changed) return;
        SaveDoc();
        if (placed) _hub?.Broadcast(DocFrame());
        RebuildSnapshot();
        MarkDirty(DirtyLayout | DirtyMonitor);
    }

    private static void ApplyBindings()
    {
        _hub?.SetBindAddresses(_networks.Where(n => n.Running)
            .Select(n => IPAddress.TryParse(StripPrefix(n.Ipv4), out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToList());
    }

    private static async Task MaintenanceLoopAsync()
    {
        while (!ExitTcs.Task.IsCompleted)
        {
            try
            {
                RefreshLocalMonitors();
                ConnectPeers();
                UpdateHooks();
                if (_notice is { } n && Environment.TickCount64 > n.Until)
                {
                    _notice = null;
                    MarkDirty(DirtyStatus);
                }
            }
            catch
            {
            }
            await Task.WhenAny(ExitTcs.Task, Task.Delay(MaintenanceIntervalMs));
        }
    }

    /// <summary>连接在线但尚未连上的已配对设备。</summary>
    private static void ConnectPeers()
    {
        var hub = _hub;
        if (hub is null) return;
        List<PairedPeer> peers;
        lock (StateLock) peers = [.. _peers];
        long now = Environment.TickCount64;

        lock (ConnectLock)
        foreach (var peer in peers)
        {
            if (hub.IsConnected(peer.Id))
            {
                OnlineSince.Remove(peer.Id);
                continue;
            }
            var candidates = ServicePeersOf(peer.Id).ToList();
            if (candidates.Count == 0)
            {
                OnlineSince.Remove(peer.Id);
                continue;
            }
            if (!OnlineSince.TryGetValue(peer.Id, out var since)) OnlineSince[peer.Id] = since = now;
            bool mineToDial = string.CompareOrdinal(LocalId, peer.Id) < 0 || now - since > FallbackDialDelayMs;
            if (!mineToDial) continue;

            // 多个网络都能到达时轮流尝试
            int attempt = DialAttempts.GetValueOrDefault(peer.Id);
            DialAttempts[peer.Id] = attempt + 1;
            var sp = candidates[attempt % candidates.Count];
            if (IPAddress.TryParse(sp.Ipv4, out var ip))
            {
                hub.Connect(peer.Id, new IPEndPoint(ip, sp.Port > 0 ? sp.Port : Protocol.DefaultPort));
            }
        }
    }

    private static void UpdateHooks()
    {
        lock (ConnectLock)
        {
            bool want = _settings.Enabled && (_hub?.ConnectedIds.Count ?? 0) > 0;
            if (want == _hooksWanted) return;
            _hooksWanted = want;
            _engine?.SetHooks(want);
        }
    }

    // ---------------- 排列 ----------------

    private static string LocalName()
    {
        var host = _networks.FirstOrDefault(n => n.Running && !string.IsNullOrWhiteSpace(n.Hostname))?.Hostname;
        return string.IsNullOrWhiteSpace(host) ? Environment.MachineName : host.Trim();
    }

    /// <summary>本机与已配对设备（排列中需要显示的设备），本机在前，其余按添加顺序。</summary>
    private static List<string> KnownIds()
    {
        var ids = new List<string> { LocalId };
        ids.AddRange(_peers.Select(p => p.Id));
        return ids;
    }

    private static void RefreshLocalMonitors()
    {
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0) return;
        string name = LocalName();
        bool changed;
        lock (StateLock)
        {
            if (!_doc.Devices.TryGetValue(LocalId, out var self))
            {
                self = new DeviceEntry { Rev = _doc.NextRev() };
                _doc.Devices[LocalId] = self;
            }
            changed = self.Name != name || self.Monitors.Count != monitors.Count ||
                      self.Monitors.Zip(monitors).Any(p => !p.First.SameGeometry(p.Second));
            if (changed)
            {
                self.Name = name;
                self.Monitors = monitors;
                self.MRev = _doc.NextRev();
            }
        }
        if (!changed) return;
        SaveDoc();
        _hub?.Broadcast(DocFrame());
        RebuildSnapshot();
        MarkDirty(DirtyLayout | DirtyMonitor);
    }

    private static void RebuildSnapshot()
    {
        var engine = _engine;
        if (engine is null) return;
        List<PlacedMonitor> placed;
        lock (StateLock)
        {
            var online = KnownIds().Where(id => id == LocalId || (_hub?.IsConnected(id) ?? false));
            placed = Geometry.PlaceAll(_doc, online);
        }
        engine.UpdateSnapshot(new LayoutSnapshot(LocalId, placed));
    }

    private static void MoveDevice(JsonElement v)
    {
        if (!v.TryGetProperty("group", out var g) || g.GetString() is not { } id) return;
        double dx = v.TryGetProperty("dx", out var x) && x.TryGetDouble(out var dxv) ? dxv : 0;
        double dy = v.TryGetProperty("dy", out var y) && y.TryGetDouble(out var dyv) ? dyv : 0;
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;

        lock (StateLock)
        {
            if (!_doc.Devices.TryGetValue(id, out var entry)) return;
            entry.X = Math.Round(entry.X + dx, 2);
            entry.Y = Math.Round(entry.Y + dy, 2);
            entry.Rev = _doc.NextRev();
        }
        CommitLayoutChange();
    }

    private static void SetScale(string value)
    {
        lock (StateLock)
        {
            if (!TrySelected(out var entry, out var mon)) return;
            var setting = entry.SettingFor(mon.Id).Clone();
            if (value == MonitorSetting.ModeSize)
            {
                setting.Mode = MonitorSetting.ModeSize;
                if (setting.Inches < 3) setting.Inches = Geometry.DefaultInches(mon);
            }
            else if (int.TryParse(value, out int percent) && ScalePercents.Contains(percent))
            {
                setting.Mode = MonitorSetting.ModeScale;
                setting.ScalePercent = percent;
            }
            else
            {
                setting.Mode = MonitorSetting.ModeAuto;
            }
            entry.Settings[mon.Id] = setting;
            entry.Rev = _doc.NextRev();
            ResolvePosition(SelectedDevice()!);
        }
        CommitLayoutChange();
    }

    private static void SetInches(double inches)
    {
        if (!double.IsFinite(inches)) return;
        lock (StateLock)
        {
            if (!TrySelected(out var entry, out var mon)) return;
            var setting = entry.SettingFor(mon.Id).Clone();
            setting.Mode = MonitorSetting.ModeSize;
            setting.Inches = Math.Round(Math.Clamp(inches, 5, 150), 1);
            entry.Settings[mon.Id] = setting;
            entry.Rev = _doc.NextRev();
            ResolvePosition(SelectedDevice()!);
        }
        CommitLayoutChange();
    }

    /// <summary>显示器大小变化后，保证该设备仍与其他设备相接且不重叠。调用方持有 StateLock。</summary>
    private static void ResolvePosition(string deviceId)
    {
        var all = Geometry.PlaceAll(_doc, KnownIds());
        var mine = all.Where(m => m.DeviceId == deviceId).ToList();
        var others = all.Where(m => m.DeviceId != deviceId).ToList();
        var (dx, dy) = Geometry.Resolve(mine, others);
        if (dx == 0 && dy == 0) return;
        var entry = _doc.Devices[deviceId];
        entry.X = Math.Round(entry.X + dx, 2);
        entry.Y = Math.Round(entry.Y + dy, 2);
    }

    private static void CommitLayoutChange()
    {
        SaveDoc();
        _hub?.Broadcast(DocFrame());
        RebuildSnapshot();
        MarkDirty(DirtyLayout | DirtyMonitor);
    }

    private static byte[] DocFrame()
    {
        lock (StateLock) return Protocol.JsonFrame(MsgType.Layout, _doc);
    }

    /// <summary>编号：本机显示器在前（主显示器为 1），其余设备按添加顺序，组内从左到右、从上到下。</summary>
    private static List<(PlacedMonitor Monitor, string Number)> Numbered()
    {
        var list = new List<(PlacedMonitor, string)>();
        int n = 1;
        foreach (var id in KnownIds())
        {
            if (!_doc.Devices.TryGetValue(id, out var entry)) continue;
            foreach (var m in Geometry.PlaceDevice(id, entry).OrderByDescending(m => m.Primary).ThenBy(m => m.Layout.X).ThenBy(m => m.Layout.Y))
            {
                list.Add((m, (n++).ToString()));
            }
        }
        return list;
    }

    private static void Identify()
    {
        List<(PlacedMonitor Monitor, string Number)> numbered;
        lock (StateLock) numbered = Numbered();
        foreach (var group in numbered.GroupBy(x => x.Monitor.DeviceId))
        {
            var numbers = group.ToDictionary(x => x.Monitor.MonitorId, x => x.Number, StringComparer.Ordinal);
            if (group.Key == LocalId) _engine?.ShowIdentify(numbers);
            else _hub?.Send(group.Key, Protocol.JsonFrame(MsgType.Identify, new IdentifyMsg { Numbers = numbers }));
        }
    }

    private static string? SelectedDevice() => _selected?.Split('|', 2)[0];

    /// <summary>当前选中的显示器（无效时回到本机主显示器）。调用方持有 StateLock。</summary>
    private static bool TrySelected(out DeviceEntry entry, out MonitorInfo monitor)
    {
        entry = null!;
        monitor = null!;
        var parts = _selected?.Split('|', 2);
        if (parts is { Length: 2 } && _doc.Devices.TryGetValue(parts[0], out var e) &&
            e.Monitors.FirstOrDefault(m => m.Id == parts[1]) is { } m && KnownIds().Contains(parts[0]))
        {
            entry = e;
            monitor = m;
            return true;
        }
        if (_doc.Devices.TryGetValue(LocalId, out var self) && self.Monitors.Count > 0)
        {
            var primary = self.Monitors.FirstOrDefault(x => x.Primary) ?? self.Monitors[0];
            _selected = $"{LocalId}|{primary.Id}";
            entry = self;
            monitor = primary;
            return true;
        }
        return false;
    }

    private static async Task AfterSelectionChangedAsync()
    {
        bool showSize;
        lock (StateLock) showSize = TrySelected(out var e, out var m) && e.SettingFor(m.Id).Mode == MonitorSetting.ModeSize;
        if (showSize != _sizeFieldShown) await SafeAsync(RebuildSchemaAsync);
        else MarkDirty(DirtyMonitor | DirtyLayout);
    }

    // ---------------- 界面 ----------------

    private static void MarkDirty(int flags) => Interlocked.Or(ref _dirty, flags);

    private static void SetNotice(string text)
    {
        _notice = (text, Environment.TickCount64 + 8000);
        MarkDirty(DirtyStatus);
    }

    private static async Task UiLoopAsync()
    {
        while (!ExitTcs.Task.IsCompleted)
        {
            int flags = Interlocked.Exchange(ref _dirty, 0);
            if (flags != 0) await SafeAsync(() => PushValuesAsync(flags));
            await Task.WhenAny(ExitTcs.Task, Task.Delay(120));
        }
    }

    private static Task PushValuesAsync(int flags)
    {
        var values = new Dictionary<string, object?>();
        lock (StateLock)
        {
            if ((flags & DirtyStatus) != 0)
            {
                values["status"] = BuildStatusText();
                values["btn_toggle"] = new { label = _settings.Enabled ? "暂停共享" : "恢复共享", disabled = false, danger = false };
            }
            if ((flags & DirtyLayout) != 0) values["layout"] = BuildLayoutValue();
            if ((flags & DirtyDevices) != 0) values["devices"] = BuildDeviceRows();
            if ((flags & DirtyMonitor) != 0)
            {
                foreach (var (k, v) in BuildMonitorValues()) values[k] = v;
            }
        }
        return UpdateValuesAsync(values);
    }

    private static async Task RebuildSchemaAsync()
    {
        if (_ipc is null) return;
        UiPageSchema schema;
        lock (StateLock) schema = BuildSchema();
        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams { MenuId = MainMenuId, Schema = schema });
        MarkDirty(DirtyStatus | DirtyLayout | DirtyDevices | DirtyMonitor);
    }

    private static string DeviceName(string? id)
    {
        if (id is null) return string.Empty;
        if (_doc.Devices.TryGetValue(id, out var e) && !string.IsNullOrWhiteSpace(e.Name)) return e.Name;
        return _peers.FirstOrDefault(p => p.Id == id)?.Name ?? id[..Math.Min(8, id.Length)];
    }

    private static string BuildStatusText()
    {
        var state = _engine?.Current;
        string text;
        if (!_settings.Enabled) text = "已暂停";
        else if (state is { Remote: true, TargetDevice: { } target }) text = $"正在控制 {DeviceName(target)}";
        else if (state?.ControlledBy is { } by) text = $"{DeviceName(by)} 正在控制本机";
        else if (_hub?.LastBindError is { } err) text = $"本机共享服务异常：{err}";
        else if (_peers.Count == 0) text = "未添加设备";
        else
        {
            int connected = _peers.Count(p => _hub?.IsConnected(p.Id) == true);
            text = connected == 0 ? "已添加的设备都不在线" : $"共享中 · 已连接 {connected} 台设备";
        }
        if (_notice is { } n && Environment.TickCount64 <= n.Until) text += $" · {n.Text}";
        return text;
    }

    private static object BuildLayoutValue()
    {
        TrySelected(out _, out _);
        var state = _engine?.Current;
        var screens = new List<object>();
        foreach (var (m, number) in Numbered())
        {
            bool self = m.DeviceId == LocalId;
            bool online = self || _hub?.IsConnected(m.DeviceId) == true;
            var mon = _doc.Devices[m.DeviceId].Monitors.First(x => x.Id == m.MonitorId);
            screens.Add(new
            {
                id = $"{m.DeviceId}|{m.MonitorId}",
                group = m.DeviceId,
                x = Math.Round(m.Layout.X, 2),
                y = Math.Round(m.Layout.Y, 2),
                w = Math.Round(m.Layout.W, 2),
                h = Math.Round(m.Layout.H, 2),
                number,
                title = DeviceName(m.DeviceId),
                subtitle = self ? $"本机 · {mon.W} × {mon.H}" : $"{mon.W} × {mon.H}",
                tone = self ? "self" : online ? "peer" : "offline",
                active = state is not null && state.ActiveDevice == m.DeviceId && state.ActiveMonitor == m.MonitorId
            });
        }
        return new { selected = _selected, screens };
    }

    private static List<Dictionary<string, object?>> BuildDeviceRows()
    {
        var ids = new List<string>();
        ids.AddRange(_peers.Select(p => p.Id));
        foreach (var sp in _servicePeers)
        {
            if (MetaId(sp) is { } id && id != LocalId && !ids.Contains(id)) ids.Add(id);
        }

        return ids.Select(id =>
        {
            var peer = _peers.FirstOrDefault(p => p.Id == id);
            var sp = ServicePeersOf(id).FirstOrDefault() ?? _servicePeers.FirstOrDefault(s => MetaId(s) == id);
            bool connected = peer is not null && _hub?.IsConnected(id) == true;
            bool compatible = sp is null || MetaVersion(sp) == Protocol.Version;
            bool pairing = Pairing.Contains(id);
            string state = peer is not null
                ? connected ? "已连接" : sp is not null ? "连接中" : "离线"
                : pairing ? "等待对方同意" : compatible ? "未添加" : "版本不兼容";
            return new Dictionary<string, object?>
            {
                ["row_id"] = id,
                ["name"] = peer is not null ? DeviceName(id) : sp?.Hostname ?? id,
                ["ipv4"] = sp?.Ipv4 ?? "-",
                ["network"] = sp?.NetworkName ?? "-",
                ["state"] = state,
                ["can_add"] = peer is null && !pairing && sp is not null && compatible,
                ["can_remove"] = peer is not null
            };
        })
        .OrderBy(r => (bool)r["can_remove"]! ? 0 : 1)
        .ThenBy(r => (string)r["name"]!, StringComparer.CurrentCulture)
        .ToList();
    }

    private static Dictionary<string, object?> BuildMonitorValues()
    {
        var values = new Dictionary<string, object?>();
        if (!TrySelected(out var entry, out var mon))
        {
            values["mon_name"] = "-";
            values["mon_res"] = "-";
            return values;
        }
        string deviceId = SelectedDevice()!;
        string number = Numbered().FirstOrDefault(x => x.Monitor.DeviceId == deviceId && x.Monitor.MonitorId == mon.Id).Number ?? "-";
        var setting = entry.SettingFor(mon.Id);
        values["mon_name"] = $"{number} · {DeviceName(deviceId)}";
        values["mon_res"] = $"{mon.W} × {mon.H} · 系统缩放 {Math.Round(mon.Scale * 100)}%";
        values["mon_scale"] = setting.Mode switch
        {
            MonitorSetting.ModeSize => MonitorSetting.ModeSize,
            MonitorSetting.ModeScale => ((int)Math.Round(setting.ScalePercent)).ToString(),
            _ => MonitorSetting.ModeAuto
        };
        values["mon_inches"] = setting.Inches >= 3 ? setting.Inches : Geometry.DefaultInches(mon);
        return values;
    }

    private static UiPageSchema BuildSchema()
    {
        var sections = new List<UiSection>();

        if (_pendingConsent is { } pending)
        {
            sections.Add(new UiSection
            {
                Id = "consent",
                Title = "添加请求",
                Compact = true,
                Items =
                [
                    new UiItem { Key = "consent_status", Type = "status", Value = J(ConsentText(pending)) },
                    new UiItem { Key = "btn_reject", Type = "button", Label = "拒绝", Danger = true },
                    new UiItem { Key = "btn_accept", Type = "button", Label = "同意" }
                ]
            });
        }

        sections.Add(new UiSection
        {
            Id = "overview",
            Compact = true,
            Items =
            [
                new UiItem { Key = "status", Type = "status", Value = J(BuildStatusText()) },
                new UiItem { Key = "btn_identify", Type = "button", Label = "识别" },
                new UiItem { Key = "btn_toggle", Type = "button", Label = _settings.Enabled ? "暂停共享" : "恢复共享" }
            ]
        });

        sections.Add(new UiSection
        {
            Id = "arrange",
            Title = "屏幕排列",
            Items =
            [
                new UiItem { Key = "layout", Type = "screen_layout", Placeholder = "正在读取显示器", Value = J(BuildLayoutValue()) }
            ]
        });

        bool sizeMode = TrySelected(out var entry, out var mon) && entry.SettingFor(mon.Id).Mode == MonitorSetting.ModeSize;
        _sizeFieldShown = sizeMode;
        var monitorItems = new List<UiItem>
        {
            new() { Key = "mon_name", Type = "status", Label = "名称" },
            new() { Key = "mon_res", Type = "status", Label = "分辨率" },
            new()
            {
                Key = "mon_scale",
                Type = "select",
                Label = "大小",
                Options = [Option("跟随系统", MonitorSetting.ModeAuto), .. ScalePercents.Select(p => Option($"{p}%", p.ToString())), Option("按屏幕尺寸", MonitorSetting.ModeSize)]
            }
        };
        if (sizeMode)
        {
            monitorItems.Add(new UiItem { Key = "mon_inches", Type = "number", Label = "屏幕尺寸（英寸）", Min = 5, Max = 150, Step = 0.1 });
        }
        sections.Add(new UiSection { Id = "monitor", Title = "显示器", Items = monitorItems });

        sections.Add(new UiSection
        {
            Id = "devices_section",
            Title = "设备",
            Items =
            [
                new UiItem
                {
                    Key = "devices",
                    Type = "table",
                    Placeholder = "暂无发现运行共享鼠标的设备",
                    Columns =
                    [
                        new() { Key = "name", Label = "设备" },
                        new() { Key = "ipv4", Label = "虚拟 IP" },
                        new() { Key = "network", Label = "所属网络" },
                        new() { Key = "state", Label = "状态" }
                    ],
                    RowActions =
                    [
                        new() { Id = "add", Label = "添加", WhenKey = "can_add", WhenEquals = J(true) },
                        new() { Id = "remove", Label = "移除", WhenKey = "can_remove", WhenEquals = J(true) }
                    ],
                    Rows = []
                }
            ]
        });

        return new UiPageSchema { Sections = sections };
    }

    private static async Task UpdateValuesAsync(Dictionary<string, object?> values)
    {
        if (_ipc is null || values.Count == 0) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.UiUpdateValues, new UiUpdateValuesParams
        {
            MenuId = MainMenuId,
            Values = values.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value))
        });
    }

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static UiSelectOption Option<T>(string label, T value) => new() { Label = label, Value = J(value) };

    // ---------------- 发现 ----------------

    private static string? MetaId(ServicePeerInfo p) =>
        p.Meta.ValueKind == JsonValueKind.Object && p.Meta.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String &&
        Protocol.IsValidId(v.GetString()) ? v.GetString() : null;

    private static int MetaVersion(ServicePeerInfo p) =>
        p.Meta.ValueKind == JsonValueKind.Object && p.Meta.TryGetProperty("v", out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static IEnumerable<ServicePeerInfo> ServicePeersOf(string id) =>
        _servicePeers.Where(p => MetaId(p) == id && MetaVersion(p) == Protocol.Version)
            .OrderBy(p => string.Equals(p.RouteType, "p2p", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => double.TryParse(p.LatencyMs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : double.MaxValue);

    private static ServicePeerInfo? FindServicePeer(string id) => ServicePeersOf(id).FirstOrDefault();

    // ---------------- 持久化 ----------------

    private static void LoadState()
    {
        _settings = LoadJson<ShareSettings>("settings.json") ?? new ShareSettings();
        if (string.IsNullOrWhiteSpace(_settings.DeviceId) || _settings.DeviceId.Contains('|'))
        {
            _settings.DeviceId = Guid.NewGuid().ToString("N");
        }
        SaveJson("settings.json", _settings);
        _peers = (LoadJson<List<PairedPeer>>("peers.json") ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && p.Id != _settings.DeviceId && !p.Id.Contains('|'))
            .GroupBy(p => p.Id)
            .Select(g => g.Last())
            .ToList();
        _doc = LoadJson<LayoutDoc>("layout.json") ?? new LayoutDoc();
        var known = KnownIds().ToHashSet();
        foreach (var id in _doc.Devices.Keys.Where(id => !known.Contains(id)).ToList()) _doc.Devices.Remove(id);
    }

    private static void SaveDoc() => SaveJson("layout.json", _doc);

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
            string json;
            lock (StateLock) json = JsonSerializer.Serialize(value, FileJsonOptions);
            var path = Path.Combine(_dataDir, name);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch
        {
        }
    }

    // ---------------- 工具 ----------------

    private static string? StripPrefix(string? ip) => ip?.Split('/')[0].Trim();

    private static async Task SafeAsync(Func<Task> action)
    {
        try { await action(); } catch { /* 宿主已断开或正在退出 */ }
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
}

internal sealed class ShareSettings
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

internal sealed class PairedPeer
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    /// <summary>配对密钥（base64），仅用于连接时双方互相验证。</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    /// <summary>本机发起添加、尚未把对方放到排列中。</summary>
    [JsonPropertyName("needs_placement")] public bool NeedsPlacement { get; set; }
}
