using System.IO.Pipes;
using System.Text.Json;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using ScreenShare.Capture;
using ScreenShare.Streaming;
using ScreenShare.Viewer;

namespace ScreenShare;

internal static class Program
{
    private const string MainMenuId = "main";

    private static ShareSettings Settings = new();
    private static string _settingsPath = string.Empty;
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };
    private static readonly TaskCompletionSource ExitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly SemaphoreSlim RendererInitLock = new(1, 1);

    private static JsonRpcPipeConnection? _ipc;
    private static ScreenShareServer? _server;
    private static ScreenShareClient? _client;
    private static EmbeddedD3D11Renderer? _renderer;
    private static List<HostNetworkStatus> _networks = [];
    private static List<ServicePeerInfo> _servicePeers = [];
    private static List<CapturableWindowInfo> _capturableWindows = [];
    private static bool _sharingEnabled;
    private static bool _inViewerMode;

    public static async Task Main(string[] args)
    {
        string pipeArg = GetArg(args, "--pipe") ?? @"\\.\pipe\easytier_ext_ipc";
        string pluginId = GetArg(args, "--plugin-id") ?? "screen-share";
        string token = GetArg(args, "--token") ?? string.Empty;
        string dataDir = GetArg(args, "--data-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Easytier_With_Extension",
            "plugins_data",
            pluginId);
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Combine(dataDir, "settings.json");
        LoadSettings();

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
        RefreshCapturableWindows();
        _server = new ScreenShareServer(Settings);
        _server.StateChanged += () => _ = PushMetaUpdateAsync();
        _server.CaptureTargetClosed += () => _ = HandleCaptureTargetClosedAsync();

        // Register unified single menu declarative UI schema
        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
        {
            MenuId = MainMenuId,
            Schema = BuildSettingsPeersSchema()
        });

        // Register in EasyTier network discovery
        await _ipc.SendRequestAsync<object>(IpcMethods.DiscoveryRegister, new DiscoveryRegisterParams
        {
            Port = ScreenShareServer.ControlPort,
            Proto = "tcp",
            Meta = BuildMetaElement()
        });

        _servicePeers = await _ipc.SendRequestAsync<List<ServicePeerInfo>>(IpcMethods.DiscoveryGetServicePeers) ?? [];
        await RefreshPeersTableAsync();

        await ExitTcs.Task;

        if (_client is not null) await _client.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        _renderer?.Dispose();
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
                if (ev is not null)
                {
                    _networks = ev.Networks;
                    if (_sharingEnabled && _server is not null)
                    {
                        StartServerOnSelectedNetworks();
                    }
                    await RefreshNetworkSelectAsync();
                }
                break;
            }
            case IpcMethods.EventDiscoveryUpdated:
            {
                var ev = @params.Value.Deserialize<EventDiscoveryUpdatedParams>(IpcJsonContext.Options);
                if (ev is not null)
                {
                    _servicePeers = ev.ServicePeers;
                    await RefreshPeersTableAsync();
                }
                break;
            }
            case IpcMethods.EventViewportChanged:
            {
                var ev = @params.Value.Deserialize<EventViewportChangedParams>(IpcJsonContext.Options);
                if (ev is not null && ev.MenuId == MainMenuId)
                {
                    if (_inViewerMode && ev.Visible && _renderer is null)
                    {
                        await EnsureRendererInitializedAsync(ev.Width, ev.Height, ev.Visible);
                    }
                    _renderer?.Resize(ev.Width, ev.Height, _inViewerMode && ev.Visible);
                }
                break;
            }
            case IpcMethods.EventUiAction:
            {
                var ev = @params.Value.Deserialize<EventUiActionParams>(IpcJsonContext.Options);
                if (ev is not null)
                {
                    await HandleUiActionAsync(ev);
                }
                break;
            }
            case IpcMethods.EventShutdown:
                ExitTcs.TrySetResult();
                break;
        }
    }

    private static async Task HandleUiActionAsync(EventUiActionParams ev)
    {
        if (ev.MenuId != MainMenuId) return;

        if (!_inViewerMode)
        {
            switch (ev.Key)
            {
                case "peers_table" when (ev.Action == "watch" || ev.Action == "control_watch") && !string.IsNullOrWhiteSpace(ev.RowId):
                {
                    var target = _servicePeers.FirstOrDefault(p => $"{p.NetworkId}|{p.Ipv4}" == ev.RowId);
                    if (target is not null && _ipc is not null)
                    {
                        _inViewerMode = true;
                        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
                        {
                            MenuId = MainMenuId,
                            Schema = BuildViewerSchema()
                        });

                        await EnsureRendererInitializedAsync(800, 600, true);
                        if (_client is not null)
                        {
                            var localIp = _networks.FirstOrDefault(n => n.NetworkId == target.NetworkId)?.Ipv4;
                            try
                            {
                                await _client.ConnectAsync(
                                    target.Ipv4,
                                    target.Port > 0 ? target.Port : ScreenShareServer.ControlPort,
                                    localIp,
                                    $"{target.Hostname} ({target.Ipv4})",
                                    autoRequestControl: ev.Action == "control_watch");
                            }
                            catch
                            {
                                await ExitViewerModeAsync();
                            }
                        }
                    }
                    break;
                }
                case "sharing_enabled":
                    _sharingEnabled = ev.Value?.ValueKind == JsonValueKind.True;
                    if (_sharingEnabled)
                    {
                        if (Settings.CaptureSource == "window")
                        {
                            RefreshCapturableWindows();
                            if (!WindowEnumerator.IsValidWindow(Settings.TargetWindowHwnd))
                            {
                                _sharingEnabled = false;
                                await RefreshNetworkSelectAsync();
                                await PushMetaUpdateAsync();
                                break;
                            }
                        }
                        StartServerOnSelectedNetworks();
                    }
                    else
                    {
                        _server?.Stop();
                    }
                    await PushMetaUpdateAsync();
                    break;
                case "target_network":
                    Settings.TargetNetworkId = ev.Value?.GetString() ?? "__all__";
                    SaveSettings();
                    if (_sharingEnabled) StartServerOnSelectedNetworks();
                    await PushMetaUpdateAsync();
                    break;
                case "share_audio":
                    Settings.ShareAudio = ev.Value?.ValueKind == JsonValueKind.True;
                    SaveSettings();
                    if (_sharingEnabled) StartServerOnSelectedNetworks();
                    await PushMetaUpdateAsync();
                    break;
                case "allow_control":
                    Settings.AllowRemoteControl = ev.Value?.ValueKind == JsonValueKind.True;
                    SaveSettings();
                    if (_server is not null)
                    {
                        await _server.UpdateControlPolicyAsync();
                    }
                    await PushMetaUpdateAsync();
                    break;
                case "capture_source":
                    Settings.CaptureSource = ev.Value?.GetString() == "window" ? "window" : "fullscreen";
                    if (Settings.CaptureSource == "window")
                    {
                        RefreshCapturableWindows();
                    }
                    SaveSettings();
                    if (_sharingEnabled)
                    {
                        if (Settings.CaptureSource == "window" && !WindowEnumerator.IsValidWindow(Settings.TargetWindowHwnd))
                        {
                            _sharingEnabled = false;
                            _server?.Stop();
                        }
                        else
                        {
                            StartServerOnSelectedNetworks();
                        }
                    }
                    await RefreshNetworkSelectAsync();
                    await PushMetaUpdateAsync();
                    break;
                case "target_window":
                    if (long.TryParse(ev.Value?.GetString(), out long hwndVal) && hwndVal > 0)
                    {
                        Settings.TargetWindowHwnd = new IntPtr(hwndVal);
                        var matched = _capturableWindows.FirstOrDefault(w => w.Hwnd == hwndVal);
                        if (matched is not null)
                        {
                            Settings.LastWindowTitle = matched.Title;
                        }
                        SaveSettings();
                        if (_sharingEnabled && Settings.CaptureSource == "window")
                        {
                            StartServerOnSelectedNetworks();
                        }
                    }
                    break;
                case "btn_refresh_windows":
                    RefreshCapturableWindows();
                    await RefreshNetworkSelectAsync();
                    break;
                case "capture_api":
                    Settings.CaptureApi = ev.Value?.GetString() ?? "wgc";
                    SaveSettings();
                    if (_sharingEnabled && Settings.CaptureSource != "window") StartServerOnSelectedNetworks();
                    break;
                case "resolution":
                    Settings.Resolution = ResolutionPresets.Normalize(ev.Value?.GetString());
                    SaveSettings();
                    if (_sharingEnabled && Settings.CaptureSource != "window") StartServerOnSelectedNetworks();
                    await PushMetaUpdateAsync();
                    break;
                case "fps":
                    if (ev.Value?.TryGetInt32(out int fps) == true) Settings.Fps = fps;
                    SaveSettings();
                    if (_sharingEnabled) StartServerOnSelectedNetworks();
                    await PushMetaUpdateAsync();
                    break;
                case "bitrate_mbps":
                    if (ev.Value?.TryGetInt32(out int mbps) == true) Settings.BitrateMbps = Math.Clamp(mbps, 1, 50);
                    SaveSettings();
                    if (_sharingEnabled) StartServerOnSelectedNetworks();
                    break;
            }
        }
        else if (_client is not null)
        {
            switch (ev.Key)
            {
                case "btn_mute":
                    await _client.ToggleMuteAsync();
                    break;
                case "btn_control":
                    await _client.ToggleControlAsync();
                    break;
                case "btn_disconnect":
                    await ExitViewerModeAsync();
                    break;
            }
        }
    }

    private static async Task ExitViewerModeAsync()
    {
        if (!_inViewerMode) return;
        _inViewerMode = false;

        if (_client is not null && _client.IsConnected)
        {
            await _client.DisconnectAsync();
        }
        _renderer?.Resize(1, 1, false);

        if (_ipc is not null)
        {
            await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
            {
                MenuId = MainMenuId,
                Schema = BuildSettingsPeersSchema()
            });
            await RefreshPeersTableAsync();
        }
    }

    private static async Task EnsureRendererInitializedAsync(int width, int height, bool visible)
    {
        if (_renderer is not null || _ipc is null) return;
        await RendererInitLock.WaitAsync();
        try
        {
            if (_renderer is not null) return;
            var vpHost = await _ipc.SendRequestAsync<UiGetViewportHostResult>(
                IpcMethods.UiGetViewportHost,
                new UiGetViewportHostParams { MenuId = MainMenuId });
            if (vpHost is not null && vpHost.Hwnd != 0)
            {
                _renderer = new EmbeddedD3D11Renderer(
                    new IntPtr(vpHost.Hwnd),
                    Math.Max(width, vpHost.Width),
                    Math.Max(height, vpHost.Height),
                    visible);
                _client = new ScreenShareClient(_renderer);
                _client.StateChanged += () => _ = RefreshViewerToolbarAsync();
                _client.Disconnected += () => _ = ExitViewerModeAsync();
            }
        }
        finally
        {
            RendererInitLock.Release();
        }
    }

    private static void StartServerOnSelectedNetworks()
    {
        if (_server is null) return;
        var ips = _networks
            .Where(n => n.Running && !string.IsNullOrWhiteSpace(n.Ipv4))
            .Where(n => Settings.TargetNetworkId == "__all__" || n.NetworkId == Settings.TargetNetworkId)
            .Select(n => n.Ipv4)
            .ToList();
        _server.Start(ips);
    }

    private static async Task PushMetaUpdateAsync()
    {
        if (_ipc is null) return;
        List<string>? netIds = Settings.TargetNetworkId == "__all__" ? null : [Settings.TargetNetworkId];
        await _ipc.SendRequestAsync<object>(IpcMethods.DiscoveryUpdateMeta, new DiscoveryUpdateMetaParams
        {
            Meta = BuildMetaElement(),
            NetworkIds = netIds
        });
    }

    private static async Task HandleCaptureTargetClosedAsync()
    {
        _sharingEnabled = false;
        RefreshCapturableWindows();
        await RefreshNetworkSelectAsync();
        await PushMetaUpdateAsync();
    }

    private static void RefreshCapturableWindows()
    {
        _capturableWindows = WindowEnumerator.GetCapturableWindows();
        if (_capturableWindows.Count == 0)
        {
            Settings.TargetWindowHwnd = IntPtr.Zero;
            return;
        }

        var byHwnd = Settings.TargetWindowHwnd != IntPtr.Zero
            ? _capturableWindows.FirstOrDefault(w => w.Hwnd == Settings.TargetWindowHwnd.ToInt64())
            : null;
        if (byHwnd is not null)
        {
            Settings.LastWindowTitle = byHwnd.Title;
            return;
        }

        var byTitle = !string.IsNullOrWhiteSpace(Settings.LastWindowTitle)
            ? _capturableWindows.FirstOrDefault(w => string.Equals(w.Title, Settings.LastWindowTitle, StringComparison.OrdinalIgnoreCase))
            : null;
        var chosen = byTitle ?? _capturableWindows[0];
        Settings.TargetWindowHwnd = new IntPtr(chosen.Hwnd);
        Settings.LastWindowTitle = chosen.Title;
    }

    private static JsonElement BuildMetaElement()
    {
        var spec = Settings.CaptureSource == "window"
            ? $"窗口捕获@{Settings.Fps}"
            : $"{(Settings.Resolution == "original" ? "原画" : Settings.Resolution)}@{Settings.Fps}";
        return JsonSerializer.SerializeToElement(new
        {
            sharing = _sharingEnabled,
            spec,
            audio = Settings.ShareAudio,
            control = Settings.AllowRemoteControl,
            viewers = _server?.ActiveViewerCount ?? 0
        });
    }

    private static async Task RefreshPeersTableAsync()
    {
        if (_ipc is null || _inViewerMode) return;
        var rows = new List<Dictionary<string, JsonElement>>();
        foreach (var p in _servicePeers)
        {
            bool sharing = p.Meta.ValueKind == JsonValueKind.Object &&
                           p.Meta.TryGetProperty("sharing", out var sh) &&
                           sh.ValueKind == JsonValueKind.True;
            string spec = p.Meta.ValueKind == JsonValueKind.Object &&
                          p.Meta.TryGetProperty("spec", out var sp) ? sp.GetString() ?? "-" : "-";
            bool ctrl = p.Meta.ValueKind == JsonValueKind.Object &&
                        p.Meta.TryGetProperty("control", out var ct) &&
                        ct.ValueKind == JsonValueKind.True;

            rows.Add(new Dictionary<string, JsonElement>
            {
                ["row_id"] = JsonSerializer.SerializeToElement($"{p.NetworkId}|{p.Ipv4}"),
                ["network_name"] = JsonSerializer.SerializeToElement(p.NetworkName),
                ["hostname"] = JsonSerializer.SerializeToElement(p.Hostname),
                ["ipv4"] = JsonSerializer.SerializeToElement(p.Ipv4),
                ["route_type"] = JsonSerializer.SerializeToElement(p.RouteType.ToUpperInvariant()),
                ["latency_ms"] = JsonSerializer.SerializeToElement(p.LatencyMs),
                ["spec"] = JsonSerializer.SerializeToElement(sharing ? spec : "未开启共享"),
                ["control"] = JsonSerializer.SerializeToElement(ctrl ? "已开启被控" : "未开启被控"),
                ["sharing_state"] = JsonSerializer.SerializeToElement(sharing),
                ["can_control"] = JsonSerializer.SerializeToElement(sharing && ctrl)
            });
        }

        await _ipc.SendRequestAsync<object>(IpcMethods.UiUpdateValues, new UiUpdateValuesParams
        {
            MenuId = MainMenuId,
            Values = new Dictionary<string, JsonElement>
            {
                ["peers_table"] = JsonSerializer.SerializeToElement(rows)
            }
        });
    }

    private static async Task RefreshNetworkSelectAsync()
    {
        if (_ipc is null || _inViewerMode) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
        {
            MenuId = MainMenuId,
            Schema = BuildSettingsPeersSchema()
        });
        await RefreshPeersTableAsync();
    }

    private static async Task RefreshViewerToolbarAsync()
    {
        if (_ipc is null || _client is null || !_inViewerMode) return;
        string ctrlLabel = !_client.RemoteAllowsControl
            ? "对方未开启被控"
            : (_client.IsControlling ? "释放远控 (Ctrl+Alt+Esc)" : "申请远控");

        await _ipc.SendRequestAsync<object>(IpcMethods.UiUpdateValues, new UiUpdateValuesParams
        {
            MenuId = MainMenuId,
            Values = new Dictionary<string, JsonElement>
            {
                ["viewer_status"] = JsonSerializer.SerializeToElement(_client.RemoteLabel),
                ["btn_mute"] = JsonSerializer.SerializeToElement(_client.IsMuted ? "取消静音" : "静音"),
                ["btn_control"] = JsonSerializer.SerializeToElement(new
                {
                    label = ctrlLabel,
                    disabled = !_client.RemoteAllowsControl
                })
            }
        });
    }

    private static UiPageSchema BuildSettingsPeersSchema()
    {
        var netOptions = new List<UiSelectOption>
        {
            new() { Label = "全部已连接网络", Value = JsonSerializer.SerializeToElement("__all__") }
        };
        foreach (var n in _networks)
        {
            netOptions.Add(new UiSelectOption
            {
                Label = $"{n.DisplayName} ({n.NetworkName})",
                Value = JsonSerializer.SerializeToElement(n.NetworkId)
            });
        }

        return new UiPageSchema
        {
            Sections =
            [
                new UiSection
                {
                    Id = "peers",
                    Title = "组网内共享节点",
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
                                new() { Key = "latency_ms", Label = "延迟" },
                                new() { Key = "spec", Label = "画面规格" },
                                new() { Key = "control", Label = "被控状态" }
                            ],
                            RowActions =
                            [
                                new()
                                {
                                    Id = "watch",
                                    Label = "观看",
                                    WhenKey = "sharing_state",
                                    WhenEquals = JsonSerializer.SerializeToElement(true)
                                },
                                new()
                                {
                                    Id = "control_watch",
                                    Label = "远控",
                                    WhenKey = "can_control",
                                    WhenEquals = JsonSerializer.SerializeToElement(true)
                                }
                            ],
                            Rows = []
                        }
                    ]
                },
                new UiSection
                {
                    Id = "host_control",
                    Title = "本机共享控制",
                    Items =
                    [
                        new UiItem { Key = "sharing_enabled", Type = "switch", Label = "开启桌面共享 (同组网免密直连)", Value = JsonSerializer.SerializeToElement(_sharingEnabled) },
                        new UiItem { Key = "target_network", Type = "select", Label = "生效虚拟网络", Value = JsonSerializer.SerializeToElement(Settings.TargetNetworkId), Options = netOptions },
                        new UiItem { Key = "share_audio", Type = "switch", Label = "传输系统音频 (WASAPI Loopback)", Value = JsonSerializer.SerializeToElement(Settings.ShareAudio) },
                        new UiItem { Key = "allow_control", Type = "switch", Label = "开启被控 (允许远端键鼠控制本机)", Value = JsonSerializer.SerializeToElement(Settings.AllowRemoteControl) }
                    ]
                },
                new UiSection
                {
                    Id = "capture",
                    Title = "Capture 与编码设置",
                    Items = BuildCaptureItems()
                }
            ]
        };
    }

    private static List<UiItem> BuildCaptureItems()
    {
        var items = new List<UiItem>
        {
            new()
            {
                Key = "capture_source",
                Type = "select",
                Label = "捕获模式",
                Value = JsonSerializer.SerializeToElement(Settings.CaptureSource),
                Options =
                [
                    new() { Label = "全屏桌面", Value = JsonSerializer.SerializeToElement("fullscreen") },
                    new() { Label = "指定窗口 (Window Capture)", Value = JsonSerializer.SerializeToElement("window") }
                ]
            }
        };

        if (Settings.CaptureSource == "window")
        {
            var winOptions = _capturableWindows.Count > 0
                ? _capturableWindows.Select(w => new UiSelectOption
                {
                    Label = w.DisplayLabel,
                    Value = JsonSerializer.SerializeToElement(w.Hwnd.ToString())
                }).ToList()
                : [new UiSelectOption { Label = "暂无可捕获窗口", Value = JsonSerializer.SerializeToElement("0") }];

            items.Add(new UiItem
            {
                Key = "target_window",
                Type = "select",
                Label = "目标窗口 (自动使用 WGC)",
                Value = JsonSerializer.SerializeToElement(Settings.TargetWindowHwnd.ToInt64().ToString()),
                Options = winOptions
            });
            items.Add(new UiItem
            {
                Key = "btn_refresh_windows",
                Type = "button",
                Label = "刷新窗口列表"
            });
        }
        else
        {
            items.Add(new UiItem
            {
                Key = "capture_api",
                Type = "select",
                Label = "Capture API",
                Value = JsonSerializer.SerializeToElement(Settings.CaptureApi),
                Options =
                [
                    new() { Label = "DXGI", Value = JsonSerializer.SerializeToElement("dxgi") },
                    new() { Label = "WGC", Value = JsonSerializer.SerializeToElement("wgc") }
                ]
            });
            items.Add(new UiItem
            {
                Key = "resolution",
                Type = "select",
                Label = "输出分辨率",
                Value = JsonSerializer.SerializeToElement(ResolutionPresets.Normalize(Settings.Resolution)),
                Options = ResolutionPresets.All.Select(r => new UiSelectOption
                {
                    Label = r.Label,
                    Value = JsonSerializer.SerializeToElement(r.Key)
                }).ToList()
            });
        }

        items.Add(new UiItem
        {
            Key = "fps",
            Type = "select",
            Label = "目标帧率",
            Value = JsonSerializer.SerializeToElement(Settings.Fps),
            Options =
            [
                new() { Label = "60 FPS", Value = JsonSerializer.SerializeToElement(60) },
                new() { Label = "30 FPS", Value = JsonSerializer.SerializeToElement(30) }
            ]
        });
        items.Add(new UiItem
        {
            Key = "bitrate_mbps",
            Type = "number",
            Label = "目标码率 (Mbps)",
            Value = JsonSerializer.SerializeToElement(Settings.BitrateMbps),
            Min = 1,
            Max = 50
        });

        return items;
    }

    private static UiPageSchema BuildViewerSchema() => new()
    {
        Sections =
        [
            new UiSection
            {
                Id = "toolbar",
                Title = "",
                Compact = true,
                Items =
                [
                    new UiItem { Key = "viewer_status", Type = "status", Label = "状态", Value = JsonSerializer.SerializeToElement("正在连接...") },
                    new UiItem { Key = "btn_mute", Type = "button", Label = "静音" },
                    new UiItem { Key = "btn_control", Type = "button", Label = "对方未开启被控", Disabled = true },
                    new UiItem { Key = "btn_disconnect", Type = "button", Label = "断开并返回", Danger = true }
                ]
            },
            new UiSection
            {
                Id = "canvas",
                Title = "",
                Items =
                [
                    new UiItem { Key = "main_canvas", Type = "viewport", Label = "远程画面" }
                ]
            }
        ]
    };

    private static void LoadSettings()
    {
        if (string.IsNullOrWhiteSpace(_settingsPath) || !File.Exists(_settingsPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<ShareSettings>(File.ReadAllText(_settingsPath), SettingsJsonOptions);
            if (loaded is not null)
            {
                loaded.Resolution = ResolutionPresets.Normalize(loaded.Resolution);
                loaded.CaptureSource = loaded.CaptureSource == "window" ? "window" : "fullscreen";
                Settings = loaded;
            }
        }
        catch
        {
        }
    }

    private static void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(_settingsPath)) return;
        try
        {
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, SettingsJsonOptions));
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
}
