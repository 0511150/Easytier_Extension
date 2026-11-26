using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using PeerSpeedTest.Engine;

namespace PeerSpeedTest;

internal static class Program
{
    private const string MainMenuId = "main";
    private const int MaxHistory = 50;
    private const int PingCount = 20;
    private const int PingIntervalMs = 50;
    private const string UpColor = "#3B82F6";
    private const string DownColor = "#9CA3AF";

    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };
    private static readonly TaskCompletionSource ExitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly object StateLock = new();

    private static SpeedSettings _settings = new();
    private static List<HistoryEntry> _history = [];
    private static string _dataDir = string.Empty;

    private static JsonRpcPipeConnection? _ipc;
    private static SpeedTestServer? _server;
    private static List<HostNetworkStatus> _networks = [];
    private static List<ServicePeerInfo> _servicePeers = [];
    private static readonly Dictionary<string, string> LastResults = new(StringComparer.Ordinal);

    // 本机发起的测速
    private static CancellationTokenSource? _testCts;
    private static string? _testingRowId;
    private static string _phase = string.Empty;

    // 曲线数据（保留最近一次测速）
    private static string _chartPeer = string.Empty;
    private static PingResult? _ping;
    private static readonly List<SpeedSample> UpSamples = [];
    private static readonly List<SpeedSample> DownSamples = [];
    private static PhaseResult? _upResult;
    private static PhaseResult? _downResult;

    // 他人发来的测速请求
    private static PendingConsent? _pendingConsent;

    public static async Task Main(string[] args)
    {
        string pipeArg = GetArg(args, "--pipe") ?? @"\\.\pipe\easytier_ext_ipc";
        string pluginId = GetArg(args, "--plugin-id") ?? "peer-speedtest";
        string token = GetArg(args, "--token") ?? string.Empty;
        _dataDir = GetArg(args, "--data-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Easytier_With_Extension", "plugins_data", pluginId);
        Directory.CreateDirectory(_dataDir);
        _settings = LoadJson<SpeedSettings>("settings.json") ?? new SpeedSettings();
        _settings.Normalize();
        _history = LoadJson<List<HistoryEntry>>("history.json") ?? [];

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

        _server = new SpeedTestServer(AskConsentAsync);
        _server.ActivityChanged += () => _ = SafeAsync(RefreshStatusAsync);
        ApplyServerBindings();

        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
        {
            MenuId = MainMenuId,
            Schema = BuildSchema()
        });

        await _ipc.SendRequestAsync<object>(IpcMethods.DiscoveryRegister, new DiscoveryRegisterParams
        {
            Port = SpeedProtocol.DefaultPort,
            Proto = "tcp",
            Meta = JsonSerializer.SerializeToElement(new { v = SpeedProtocol.Version })
        });

        _servicePeers = await _ipc.SendRequestAsync<List<ServicePeerInfo>>(IpcMethods.DiscoveryGetServicePeers) ?? [];
        await RefreshAllAsync();

        await ExitTcs.Task;

        _testCts?.Cancel();
        _pendingConsent?.Tcs.TrySetResult(false);
        await _server.DisposeAsync();
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
                ApplyServerBindings();
                await RefreshStatusAsync();
                break;
            }
            case IpcMethods.EventDiscoveryUpdated:
            {
                var ev = @params.Value.Deserialize<EventDiscoveryUpdatedParams>(IpcJsonContext.Options);
                if (ev is null) break;
                _servicePeers = ev.ServicePeers;
                await RefreshPeersTableAsync();
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
            case "peers_table" when ev.Action == "test" && !string.IsNullOrEmpty(ev.RowId):
            {
                var peer = _servicePeers.FirstOrDefault(p => RowId(p) == ev.RowId);
                if (peer is not null) StartTest(peer);
                break;
            }
            case "streams" when ev.Value is { ValueKind: JsonValueKind.Number } v:
                _settings.Streams = v.GetInt32();
                _settings.Normalize();
                SaveJson("settings.json", _settings);
                break;
            case "btn_accept":
                _pendingConsent?.Tcs.TrySetResult(true);
                break;
            case "btn_reject":
                _pendingConsent?.Tcs.TrySetResult(false);
                break;
            case "btn_cancel":
                if (_testCts is not null) _testCts.Cancel();
                else _server?.AbortSession();
                break;
            case "btn_clear_history":
                lock (StateLock) _history.Clear();
                SaveJson("history.json", _history);
                await RefreshHistoryAsync();
                break;
        }
    }

    // ---------------- 被测方：确认请求 ----------------

    private static async Task<bool> AskConsentAsync(ConsentRequest request, CancellationToken ct)
    {
        var pending = new PendingConsent(request, DateTime.UtcNow.AddMilliseconds(SpeedProtocol.ConsentTimeoutMs));
        lock (StateLock)
        {
            // 自己正在测速或已有待确认请求时直接拒绝，避免两路测速互相干扰
            if (_pendingConsent is not null || _testCts is not null) return false;
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
        return $"{p.Request.Hostname}（{p.Request.RemoteIp}）请求与你测速：上传 15 秒 + 下载 15 秒 · {left} 秒后自动拒绝";
    }

    // ---------------- 发起方：测速流程 ----------------

    private static void StartTest(ServicePeerInfo peer)
    {
        CancellationToken token;
        lock (StateLock)
        {
            if (_testCts is not null || _pendingConsent is not null || _server?.CurrentSession is not null) return;
            _testCts = new CancellationTokenSource();
            token = _testCts.Token;
            _testingRowId = RowId(peer);
            _phase = "等待对方同意（最多 30 秒）";
            _chartPeer = $"{peer.Hostname}（{peer.Ipv4}）";
            _ping = null;
            _upResult = _downResult = null;
            UpSamples.Clear();
            DownSamples.Clear();
        }
        // 测速耗时较长，不能占用 IPC 事件处理（否则“停止测速”收不到）
        _ = Task.Run(() => RunTestAsync(peer, token));
    }

    private static async Task RunTestAsync(ServicePeerInfo peer, CancellationToken ct)
    {
        int streams = _settings.Streams;
        var entry = new HistoryEntry
        {
            Time = DateTime.Now,
            NetworkName = peer.NetworkName,
            Hostname = peer.Hostname,
            Ipv4 = peer.Ipv4,
            RouteType = peer.RouteType,
            Streams = streams
        };

        await SafeAsync(RefreshAllAsync);
        try
        {
            if (!IPAddress.TryParse(peer.Ipv4, out var target))
                throw new SpeedTestException("对方虚拟 IP 无效");
            var network = _networks.FirstOrDefault(n => n.NetworkId == peer.NetworkId);
            IPAddress? local = IPAddress.TryParse(StripPrefix(network?.Ipv4), out var addr) ? addr : null;
            string myName = string.IsNullOrWhiteSpace(network?.Hostname) ? Environment.MachineName : network!.Hostname;

            await using var client = new SpeedTestClient(target, peer.Port > 0 ? peer.Port : SpeedProtocol.DefaultPort, local);
            await client.RequestSessionAsync(myName, ct);

            await SetPhaseAsync("延迟测试");
            var ping = await client.PingAsync(PingCount, PingIntervalMs, ct);
            if (ping.Received == 0) throw new SpeedTestException("延迟测试无响应");
            lock (StateLock) _ping = ping;
            entry.PingMs = ping.AvgMs;
            entry.JitterMs = ping.JitterMs;

            await SetPhaseAsync("上传测试");
            var up = await client.UploadAsync(streams, SpeedProtocol.PhaseDurationMs, s => OnSample(UpSamples, s), ct);
            lock (StateLock) _upResult = up;
            entry.UpMbps = up.AverageBps / 1e6;
            entry.SentBytes = up.TotalBytes;

            await SetPhaseAsync("下载测试");
            var down = await client.DownloadAsync(streams, SpeedProtocol.PhaseDurationMs, s => OnSample(DownSamples, s), ct);
            lock (StateLock) _downResult = down;
            entry.DownMbps = down.AverageBps / 1e6;
            entry.ReceivedBytes = down.TotalBytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            entry.Error = "已取消";
        }
        catch (SpeedTestException ex)
        {
            entry.Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidDataException or TimeoutException)
        {
            entry.Error = "连接中断";
        }

        lock (StateLock)
        {
            _history.Insert(0, entry);
            if (_history.Count > MaxHistory) _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            LastResults[RowId(peer)] = entry.Summary();
            _testCts?.Dispose();
            _testCts = null;
            _testingRowId = null;
            _phase = entry.Error ?? "完成";
        }
        SaveJson("history.json", _history);
        await SafeAsync(RefreshAllAsync);
    }

    private static void OnSample(List<SpeedSample> target, SpeedSample sample)
    {
        lock (StateLock) target.Add(sample);
        _ = SafeAsync(PushChartAsync);
    }

    private static async Task SetPhaseAsync(string phase)
    {
        _phase = phase;
        await SafeAsync(RefreshStatusAsync);
        await SafeAsync(PushChartAsync);
    }

    // ---------------- 服务端 ----------------

    private static void ApplyServerBindings()
    {
        _server?.SetBindAddresses(_networks.Where(n => n.Running)
            .Select(n => IPAddress.TryParse(StripPrefix(n.Ipv4), out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToList());
    }

    private static int PeerVersion(ServicePeerInfo p) =>
        p.Meta.ValueKind == JsonValueKind.Object && p.Meta.TryGetProperty("v", out var v) && v.TryGetInt32(out var n) ? n : 1;

    // ---------------- UI ----------------

    private static async Task RefreshAllAsync()
    {
        await RefreshStatusAsync();
        await RefreshPeersTableAsync();
        await PushChartAsync();
        await RefreshHistoryAsync();
    }

    private static async Task RebuildSchemaAsync()
    {
        if (_ipc is null) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
        {
            MenuId = MainMenuId,
            Schema = BuildSchema()
        });
        await RefreshAllAsync();
    }

    private static bool Busy => _testCts is not null || _pendingConsent is not null || _server?.CurrentSession is not null;

    private static string BuildStatusText()
    {
        if (_testingRowId is not null)
            return $"正在测速 {_chartPeer} · {_phase}";
        if (_server?.CurrentSession is { } s)
            return $"正在被 {s.Hostname}（{s.RemoteIp}）测速，可随时中止";
        if (_pendingConsent is { } p)
            return $"收到 {p.Request.Hostname} 的测速请求，请在上方确认";
        if (_server?.LastBindError is { } err)
            return $"本机测速服务异常：{err}";
        if ((_server?.ListeningCount ?? 0) == 0)
            return "暂无在线虚拟网络";
        return string.IsNullOrEmpty(_phase)
            ? "空闲 · 点击节点右侧的“测速”开始，需对方同意"
            : $"空闲 · 上次测速：{_phase}";
    }

    private static Task RefreshStatusAsync()
    {
        bool testing = _testCts is not null;
        bool beingTested = !testing && _server?.CurrentSession is not null;
        return UpdateValuesAsync(new()
        {
            ["status"] = BuildStatusText(),
            ["btn_cancel"] = new
            {
                label = beingTested ? "中止被测速" : "停止测速",
                disabled = !testing && !beingTested,
                danger = testing || beingTested
            }
        });
    }

    private static async Task RefreshPeersTableAsync()
    {
        string? testing = _testingRowId;
        bool busy = Busy;
        Dictionary<string, string> lastResults;
        lock (StateLock) lastResults = new Dictionary<string, string>(LastResults);
        var rows = _servicePeers
            .OrderBy(p => p.NetworkName, StringComparer.CurrentCulture)
            .ThenBy(p => p.Hostname, StringComparer.CurrentCulture)
            .Select(p =>
            {
                string id = RowId(p);
                bool compatible = PeerVersion(p) >= SpeedProtocol.Version;
                string state = id == testing ? "测速中" : compatible ? "可测速" : "对方版本过旧";
                return new Dictionary<string, object?>
                {
                    ["row_id"] = id,
                    ["network_name"] = p.NetworkName,
                    ["hostname"] = p.Hostname,
                    ["ipv4"] = p.Ipv4,
                    ["route_type"] = string.IsNullOrEmpty(p.RouteType) ? "-" : p.RouteType.ToUpperInvariant(),
                    ["latency"] = FormatLatency(p.LatencyMs),
                    ["state"] = state,
                    ["last_result"] = lastResults.GetValueOrDefault(id, "-"),
                    ["can_test"] = compatible && !busy
                };
            })
            .ToList();
        await UpdateValuesAsync(new() { ["peers_table"] = rows });
    }

    private static Task PushChartAsync() => UpdateValuesAsync(new() { ["chart"] = BuildChartValue() });

    private static object BuildChartValue()
    {
        lock (StateLock)
        {
            static string Legend(List<SpeedSample> samples, PhaseResult? result) =>
                result is not null ? FormatBps(result.AverageBps)
                : samples.Count > 0 ? FormatBps(samples[^1].Bps)
                : "-";

            string subtitle;
            if (string.IsNullOrEmpty(_chartPeer))
            {
                subtitle = "上传 15 秒 + 下载 15 秒，每 100 毫秒采样；结果取每个方向最后 5 秒的平均值";
            }
            else
            {
                var parts = new List<string> { _chartPeer };
                if (_ping is { } ping) parts.Add($"延迟 {ping.AvgMs:0.0} ms · 抖动 {ping.JitterMs:0.0} ms");
                double upPeak = UpSamples.Count > 0 ? UpSamples.Max(s => s.Bps) : 0;
                double downPeak = DownSamples.Count > 0 ? DownSamples.Max(s => s.Bps) : 0;
                if (upPeak > 0 || downPeak > 0) parts.Add($"峰值 ↑ {FormatBps(upPeak)} ↓ {FormatBps(downPeak)}");
                subtitle = string.Join(" · ", parts);
            }

            return new
            {
                subtitle,
                x_max = SpeedProtocol.PhaseDurationMs / 1000,
                x_unit = "s",
                y_format = "bps",
                series = new object[]
                {
                    new
                    {
                        name = "上传", icon = "↑", color = UpColor, mark = _upResult?.AverageBps,
                        legend = Legend(UpSamples, _upResult),
                        points = UpSamples.Select(s => new[] { s.Seconds, s.Bps }).ToList()
                    },
                    new
                    {
                        name = "下载", icon = "↓", color = DownColor, mark = _downResult?.AverageBps,
                        legend = Legend(DownSamples, _downResult),
                        points = DownSamples.Select(s => new[] { s.Seconds, s.Bps }).ToList()
                    }
                },
                stats = new object[]
                {
                    new { label = "已发送", value = _upResult is null ? "-" : FormatBytes(_upResult.TotalBytes) },
                    new { label = "已接收", value = _downResult is null ? "-" : FormatBytes(_downResult.TotalBytes) },
                    new
                    {
                        label = "后 5 秒均值",
                        value = $"↑ {(_upResult is null ? "-" : FormatBps(_upResult.AverageBps))}  ↓ {(_downResult is null ? "-" : FormatBps(_downResult.AverageBps))}"
                    }
                }
            };
        }
    }

    private static async Task RefreshHistoryAsync()
    {
        List<Dictionary<string, object?>> rows;
        lock (StateLock)
        {
            rows = _history.Select((h, i) => new Dictionary<string, object?>
            {
                ["row_id"] = i.ToString(),
                ["time"] = h.Time.ToString("MM-dd HH:mm:ss"),
                ["peer"] = $"{h.Hostname} ({h.Ipv4})",
                ["route_type"] = string.IsNullOrEmpty(h.RouteType) ? "-" : h.RouteType.ToUpperInvariant(),
                ["ping"] = h.PingMs is { } ping ? $"{ping:0.0} / {h.JitterMs ?? 0:0.0} ms" : "-",
                ["up"] = h.UpMbps is { } u ? FormatRate(u) : "-",
                ["down"] = h.DownMbps is { } d ? FormatRate(d) : "-",
                ["note"] = h.Error ?? $"{h.Streams} 线程"
            }).ToList();
        }
        await UpdateValuesAsync(new() { ["history_table"] = rows });
    }

    private static async Task UpdateValuesAsync(Dictionary<string, object?> values)
    {
        if (_ipc is null) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.UiUpdateValues, new UiUpdateValuesParams
        {
            MenuId = MainMenuId,
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
                Title = "测速请求",
                Compact = true,
                Items =
                [
                    new UiItem { Key = "consent_status", Type = "status", Label = "请求", Value = JsonSerializer.SerializeToElement(ConsentText(pending)) },
                    new UiItem { Key = "btn_reject", Type = "button", Label = "拒绝", Danger = true },
                    new UiItem { Key = "btn_accept", Type = "button", Label = "同意" }
                ]
            });
        }

        sections.Add(new UiSection
        {
            Id = "peers",
            Title = "组网内可测速节点",
            Items =
            [
                new UiItem
                {
                    Key = "peers_table",
                    Type = "table",
                    Label = "在线节点",
                    Placeholder = "暂无发现组网内运行对端测速的节点",
                    Columns =
                    [
                        new() { Key = "network_name", Label = "所属网络" },
                        new() { Key = "hostname", Label = "节点名称" },
                        new() { Key = "ipv4", Label = "虚拟 IP" },
                        new() { Key = "route_type", Label = "链路" },
                        new() { Key = "latency", Label = "延迟" },
                        new() { Key = "state", Label = "状态" },
                        new() { Key = "last_result", Label = "最近结果" }
                    ],
                    RowActions =
                    [
                        new()
                        {
                            Id = "test",
                            Label = "测速",
                            WhenKey = "can_test",
                            WhenEquals = JsonSerializer.SerializeToElement(true)
                        }
                    ],
                    Rows = []
                }
            ]
        });

        sections.Add(new UiSection
        {
            Id = "speed",
            Title = "测速",
            Items =
            [
                new UiItem
                {
                    Key = "chart",
                    Type = "chart",
                    Label = "速率曲线",
                    Value = JsonSerializer.SerializeToElement(BuildChartValue())
                },
                new UiItem
                {
                    Key = "status",
                    Type = "status",
                    Label = "当前状态",
                    Value = JsonSerializer.SerializeToElement(BuildStatusText())
                },
                new UiItem
                {
                    Key = "streams",
                    Type = "segmented",
                    Label = "并发线程",
                    Subtitle = "多线程更容易跑满带宽，单线程更接近单个应用的实际速度",
                    Value = JsonSerializer.SerializeToElement(_settings.Streams),
                    Options = [Option("1", 1), Option("4", 4), Option("8", 8)]
                },
                new UiItem { Key = "btn_cancel", Type = "button", Label = "停止测速", Disabled = true }
            ]
        });

        sections.Add(new UiSection
        {
            Id = "history",
            Title = "测速记录",
            Items =
            [
                new UiItem
                {
                    Key = "history_table",
                    Type = "table",
                    Label = "最近记录",
                    Placeholder = "暂无测速记录",
                    Columns =
                    [
                        new() { Key = "time", Label = "时间" },
                        new() { Key = "peer", Label = "节点" },
                        new() { Key = "route_type", Label = "链路" },
                        new() { Key = "ping", Label = "延迟 / 抖动" },
                        new() { Key = "up", Label = "上传（后 5 秒）" },
                        new() { Key = "down", Label = "下载（后 5 秒）" },
                        new() { Key = "note", Label = "备注" }
                    ],
                    Rows = []
                },
                new UiItem { Key = "btn_clear_history", Type = "button", Label = "清空记录" }
            ]
        });

        return new UiPageSchema { Sections = sections };
    }

    private static UiSelectOption Option<T>(string label, T value) =>
        new() { Label = label, Value = JsonSerializer.SerializeToElement(value) };

    // ---------------- 工具方法 ----------------

    private static string RowId(ServicePeerInfo p) => $"{p.NetworkId}|{p.Ipv4}";

    private static string? StripPrefix(string? ip) => ip?.Split('/')[0].Trim();

    private static string FormatLatency(string? raw) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? $"{ms:0.#} ms"
            : string.IsNullOrWhiteSpace(raw) ? "-" : raw;

    internal static string FormatRate(double mbps) => mbps switch
    {
        >= 1000 => $"{mbps / 1000:0.00} Gbps",
        >= 100 => $"{mbps:0} Mbps",
        >= 10 => $"{mbps:0.0} Mbps",
        _ => $"{mbps:0.00} Mbps"
    };

    private static string FormatBps(double bps) => FormatRate(bps / 1e6);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.00} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.00} KB",
        _ => $"{bytes} B"
    };

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
            string json;
            lock (StateLock) json = JsonSerializer.Serialize(value, FileJsonOptions);
            File.WriteAllText(Path.Combine(_dataDir, name), json);
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

    private sealed record PendingConsent(ConsentRequest Request, DateTime Deadline)
    {
        public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class SpeedSettings
{
    public int Streams { get; set; } = 4;

    public void Normalize()
    {
        if (Streams is not (1 or 4 or 8)) Streams = 4;
    }
}

internal sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string NetworkName { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Ipv4 { get; set; } = string.Empty;
    public string RouteType { get; set; } = string.Empty;
    public int Streams { get; set; }
    public double? PingMs { get; set; }
    public double? JitterMs { get; set; }
    /// <summary>上传最后 5 秒均值（Mbps）。</summary>
    public double? UpMbps { get; set; }
    /// <summary>下载最后 5 秒均值（Mbps）。</summary>
    public double? DownMbps { get; set; }
    public long? SentBytes { get; set; }
    public long? ReceivedBytes { get; set; }
    public string? Error { get; set; }

    public string Summary()
    {
        var parts = new List<string>();
        if (UpMbps is { } u) parts.Add($"↑ {Program.FormatRate(u)}");
        if (DownMbps is { } d) parts.Add($"↓ {Program.FormatRate(d)}");
        if (parts.Count == 0 && PingMs is { } p) parts.Add($"{p:0.0} ms");
        if (Error is not null) parts.Add(Error);
        return string.Join(" · ", parts);
    }
}
