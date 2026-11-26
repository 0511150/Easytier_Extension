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

    private static CancellationTokenSource? _testCts;
    private static string? _testingRowId;
    private static string _phase = string.Empty;
    private static string _liveRate = "-";

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

        _server = new SpeedTestServer();
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
            Meta = BuildMeta()
        });

        _servicePeers = await _ipc.SendRequestAsync<List<ServicePeerInfo>>(IpcMethods.DiscoveryGetServicePeers) ?? [];
        await RefreshPeersTableAsync();
        await RefreshHistoryAsync();

        await ExitTcs.Task;

        _testCts?.Cancel();
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
            case "mode" when ev.Value is { ValueKind: JsonValueKind.String } v:
                _settings.Mode = v.GetString()!;
                SaveSettings();
                break;
            case "duration_sec" when ev.Value is { ValueKind: JsonValueKind.Number } v:
                _settings.DurationSec = v.GetInt32();
                SaveSettings();
                break;
            case "streams" when ev.Value is { ValueKind: JsonValueKind.Number } v:
                _settings.Streams = v.GetInt32();
                SaveSettings();
                break;
            case "allow_incoming" when ev.Value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v:
                _settings.AllowIncoming = v.GetBoolean();
                SaveSettings();
                ApplyServerBindings();
                await PushMetaAsync();
                await RefreshStatusAsync();
                break;
            case "btn_cancel":
                _testCts?.Cancel();
                break;
            case "btn_clear_history":
                lock (StateLock) _history.Clear();
                SaveJson("history.json", _history);
                await RefreshHistoryAsync();
                break;
        }
    }

    // ---------------- 测速流程 ----------------

    private static void StartTest(ServicePeerInfo peer)
    {
        CancellationToken token;
        lock (StateLock)
        {
            if (_testCts is not null) return;
            _testCts = new CancellationTokenSource();
            token = _testCts.Token;
            _testingRowId = RowId(peer);
            _phase = "准备中";
            _liveRate = "-";
        }
        // 测速耗时较长，不能占用 IPC 事件循环（否则“停止测速”按钮收不到）
        _ = Task.Run(() => RunTestAsync(peer, token));
    }

    private static async Task RunTestAsync(ServicePeerInfo peer, CancellationToken ct)
    {
        var settings = _settings.Clone();
        var entry = new HistoryEntry
        {
            Time = DateTime.Now,
            NetworkName = peer.NetworkName,
            Hostname = peer.Hostname,
            Ipv4 = peer.Ipv4,
            RouteType = peer.RouteType,
            Streams = settings.Streams,
            DurationSec = settings.DurationSec
        };

        await SafeAsync(RefreshAllAsync);
        try
        {
            if (!IPAddress.TryParse(peer.Ipv4, out var target))
                throw new SpeedTestException("对方虚拟 IP 无效");
            var local = LocalIpFor(peer.NetworkId);
            var client = new SpeedTestClient(target, peer.Port > 0 ? peer.Port : SpeedProtocol.DefaultPort, local);
            int durationMs = settings.DurationSec * 1000;

            await SetPhaseAsync("延迟测试");
            var ping = await client.PingAsync(PingCount, PingIntervalMs, ct);
            if (ping.Received == 0) throw new SpeedTestException("延迟测试无响应");
            entry.PingMs = ping.AvgMs;
            entry.JitterMs = ping.JitterMs;
            entry.LossRate = ping.LossRate;

            if (settings.Mode is SpeedSettings.ModeDown or SpeedSettings.ModeBoth)
            {
                await SetPhaseAsync("下载测试");
                entry.DownMbps = await client.DownloadAsync(settings.Streams, durationMs,
                    mbps => _ = SetLiveRateAsync($"↓ {FormatRate(mbps)}"), ct);
            }

            if (settings.Mode is SpeedSettings.ModeUp or SpeedSettings.ModeBoth)
            {
                await SetPhaseAsync("上传测试");
                entry.UpMbps = await client.UploadAsync(settings.Streams, durationMs,
                    mbps => _ = SetLiveRateAsync($"↑ {FormatRate(mbps)}"), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            entry.Error = "已取消";
        }
        catch (SpeedTestException ex)
        {
            entry.Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidDataException)
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
            _phase = string.Empty;
            _liveRate = "-";
        }
        SaveJson("history.json", _history);
        await SafeAsync(RefreshAllAsync);
    }

    private static IPAddress? LocalIpFor(string networkId)
    {
        var ip = _networks.FirstOrDefault(n => n.NetworkId == networkId)?.Ipv4;
        return IPAddress.TryParse(StripPrefix(ip), out var addr) ? addr : null;
    }

    private static async Task SetPhaseAsync(string phase)
    {
        _phase = phase;
        _liveRate = "-";
        await SafeAsync(RefreshStatusAsync);
    }

    private static async Task SetLiveRateAsync(string text)
    {
        _liveRate = text;
        await SafeAsync(() => UpdateValuesAsync(new() { ["live_rate"] = text }));
    }

    // ---------------- 服务端与发现 ----------------

    private static void ApplyServerBindings()
    {
        if (_server is null) return;
        var ips = _settings.AllowIncoming
            ? _networks.Where(n => n.Running)
                .Select(n => IPAddress.TryParse(StripPrefix(n.Ipv4), out var ip) ? ip : null)
                .OfType<IPAddress>()
                .ToList()
            : [];
        _server.SetBindAddresses(ips);
    }

    private static JsonElement BuildMeta() => JsonSerializer.SerializeToElement(new
    {
        v = SpeedProtocol.Version,
        accept = _settings.AllowIncoming
    });

    private static async Task PushMetaAsync()
    {
        if (_ipc is null) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.DiscoveryUpdateMeta, new DiscoveryUpdateMetaParams { Meta = BuildMeta() });
    }

    private static bool PeerAccepts(ServicePeerInfo p) =>
        p.Meta.ValueKind != JsonValueKind.Object ||
        !p.Meta.TryGetProperty("accept", out var a) ||
        a.ValueKind != JsonValueKind.False;

    // ---------------- UI ----------------

    private static async Task RefreshAllAsync()
    {
        await RefreshStatusAsync();
        await RefreshPeersTableAsync();
        await RefreshHistoryAsync();
    }

    private static string BuildStatusText()
    {
        if (_testingRowId is not null)
        {
            var peer = _servicePeers.FirstOrDefault(p => RowId(p) == _testingRowId);
            string name = peer is null ? _testingRowId.Split('|').Last() : $"{peer.Hostname} ({peer.Ipv4})";
            return $"正在测速 {name} · {_phase}";
        }

        string service;
        if (!_settings.AllowIncoming)
        {
            service = "本机已关闭被测速";
        }
        else if (_server?.LastBindError is { } err)
        {
            service = $"本机测速服务异常：{err}";
        }
        else
        {
            int listening = _server?.ListeningCount ?? 0;
            service = listening > 0 ? $"本机测速服务已在 {listening} 个网络开放" : "暂无在线虚拟网络";
        }

        int incoming = _server?.ActiveConnections ?? 0;
        return incoming > 0 ? $"空闲 · {service} · 正被其他节点测速（{incoming} 个连接）" : $"空闲 · {service}";
    }

    private static Task RefreshStatusAsync() => UpdateValuesAsync(new()
    {
        ["status"] = BuildStatusText(),
        ["live_rate"] = _liveRate,
        ["btn_cancel"] = new { label = "停止测速", disabled = _testingRowId is null, danger = _testingRowId is not null }
    });

    private static async Task RefreshPeersTableAsync()
    {
        string? testing = _testingRowId;
        bool busy = testing is not null;
        Dictionary<string, string> lastResults;
        lock (StateLock) lastResults = new Dictionary<string, string>(LastResults);
        var rows = _servicePeers
            .OrderBy(p => p.NetworkName, StringComparer.CurrentCulture)
            .ThenBy(p => p.Hostname, StringComparer.CurrentCulture)
            .Select(p =>
            {
                string id = RowId(p);
                bool accepts = PeerAccepts(p);
                string state = id == testing ? "测速中" : accepts ? "可测速" : "对方未开放";
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
                    ["can_test"] = accepts && !busy
                };
            })
            .ToList();
        await UpdateValuesAsync(new() { ["peers_table"] = rows });
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
                ["down"] = h.DownMbps is { } d ? FormatRate(d) : "-",
                ["up"] = h.UpMbps is { } u ? FormatRate(u) : "-",
                ["note"] = h.Error ?? $"{h.Streams} 线程 · {h.DurationSec} 秒"
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

    private static UiPageSchema BuildSchema() => new()
    {
        Sections =
        [
            new UiSection
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
            },
            new UiSection
            {
                Id = "settings",
                Title = "测速设置",
                Items =
                [
                    new UiItem
                    {
                        Key = "mode",
                        Type = "segmented",
                        Label = "测试项目",
                        Value = JsonSerializer.SerializeToElement(_settings.Mode),
                        Options =
                        [
                            Option("下载", SpeedSettings.ModeDown),
                            Option("上传", SpeedSettings.ModeUp),
                            Option("双向", SpeedSettings.ModeBoth)
                        ]
                    },
                    new UiItem
                    {
                        Key = "duration_sec",
                        Type = "segmented",
                        Label = "单项时长",
                        Value = JsonSerializer.SerializeToElement(_settings.DurationSec),
                        Options = [Option("5 秒", 5), Option("10 秒", 10), Option("20 秒", 20)]
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
                    new UiItem
                    {
                        Key = "allow_incoming",
                        Type = "switch",
                        Label = "允许他人对我测速",
                        Subtitle = $"仅在虚拟网卡上开放 TCP {SpeedProtocol.DefaultPort}",
                        Value = JsonSerializer.SerializeToElement(_settings.AllowIncoming)
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
                        Key = "live_rate",
                        Type = "status",
                        Label = "实时速率",
                        Value = JsonSerializer.SerializeToElement(_liveRate)
                    },
                    new UiItem { Key = "btn_cancel", Type = "button", Label = "停止测速", Disabled = true }
                ]
            },
            new UiSection
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
                        Columns =
                        [
                            new() { Key = "time", Label = "时间" },
                            new() { Key = "peer", Label = "节点" },
                            new() { Key = "route_type", Label = "链路" },
                            new() { Key = "ping", Label = "延迟 / 抖动" },
                            new() { Key = "down", Label = "下载" },
                            new() { Key = "up", Label = "上传" },
                            new() { Key = "note", Label = "备注" }
                        ],
                        Rows = []
                    },
                    new UiItem { Key = "btn_clear_history", Type = "button", Label = "清空记录" }
                ]
            }
        ]
    };

    private static UiSelectOption Option<T>(string label, T value) =>
        new() { Label = label, Value = JsonSerializer.SerializeToElement(value) };

    // ---------------- 工具方法 ----------------

    private static string RowId(ServicePeerInfo p) => $"{p.NetworkId}|{p.Ipv4}";

    private static string FormatLatency(string? raw) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? $"{ms:0.#} ms"
            : string.IsNullOrWhiteSpace(raw) ? "-" : raw;

    private static string? StripPrefix(string? ip) => ip?.Split('/')[0].Trim();

    internal static string FormatRate(double mbps) => mbps switch
    {
        >= 1000 => $"{mbps / 1000:0.00} Gbps",
        >= 100 => $"{mbps:0} Mbps",
        >= 10 => $"{mbps:0.0} Mbps",
        _ => $"{mbps:0.00} Mbps"
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

    private static void SaveSettings()
    {
        _settings.Normalize();
        SaveJson("settings.json", _settings);
    }

    private static string? GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == key) return args[i + 1];
        }
        return null;
    }
}

internal sealed class SpeedSettings
{
    public const string ModeDown = "down";
    public const string ModeUp = "up";
    public const string ModeBoth = "both";

    public string Mode { get; set; } = ModeBoth;
    public int DurationSec { get; set; } = 10;
    public int Streams { get; set; } = 4;
    public bool AllowIncoming { get; set; } = true;

    public void Normalize()
    {
        if (Mode is not (ModeDown or ModeUp or ModeBoth)) Mode = ModeBoth;
        if (DurationSec is not (5 or 10 or 20)) DurationSec = 10;
        if (Streams is not (1 or 4 or 8)) Streams = 4;
    }

    public SpeedSettings Clone() => (SpeedSettings)MemberwiseClone();
}

internal sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string NetworkName { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Ipv4 { get; set; } = string.Empty;
    public string RouteType { get; set; } = string.Empty;
    public int Streams { get; set; }
    public int DurationSec { get; set; }
    public double? PingMs { get; set; }
    public double? JitterMs { get; set; }
    public double? LossRate { get; set; }
    public double? DownMbps { get; set; }
    public double? UpMbps { get; set; }
    public string? Error { get; set; }

    public string Summary()
    {
        var parts = new List<string>();
        if (DownMbps is { } d) parts.Add($"↓ {Program.FormatRate(d)}");
        if (UpMbps is { } u) parts.Add($"↑ {Program.FormatRate(u)}");
        if (parts.Count == 0 && PingMs is { } p) parts.Add($"{p:0.0} ms");
        if (Error is not null) parts.Add(Error);
        return string.Join(" · ", parts);
    }
}
