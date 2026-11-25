using System.Collections.Concurrent;
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
    private static readonly SemaphoreSlim ThumbnailRefreshLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, string?> ThumbnailCache = new(StringComparer.Ordinal);

    private static JsonRpcPipeConnection? _ipc;
    private static ScreenShareServer? _server;
    private static ScreenShareClient? _client;
    private static EmbeddedD3D11Renderer? _renderer;
    private static List<HostNetworkStatus> _networks = [];
    private static List<ServicePeerInfo> _servicePeers = [];
    private static List<CapturableWindowInfo> _capturableWindows = [];
    private static List<CapturableMonitorInfo> _capturableMonitors = [];
    private static bool _sharingEnabled;
    private static bool _inViewerMode;
    private static string? _statusOverride;

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
        RefreshSourceListsMetadata();

        _server = new ScreenShareServer(Settings);
        _server.StateChanged += () =>
        {
            _ = PushMetaUpdateAsync();
            _ = RefreshShareStatusUiAsync();
        };
        _server.CaptureTargetClosed += () => _ = HandleCaptureTargetClosedAsync();

        // Register unified single menu declarative UI schema immediately
        await _ipc.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
        {
            MenuId = MainMenuId,
            Schema = BuildSettingsPeersSchema()
        });

        // Register in EasyTier network discovery (always across all connected virtual networks)
        await _ipc.SendRequestAsync<object>(IpcMethods.DiscoveryRegister, new DiscoveryRegisterParams
        {
            Port = ScreenShareServer.ControlPort,
            Proto = "tcp",
            Meta = BuildMetaElement()
        });

        _servicePeers = await _ipc.SendRequestAsync<List<ServicePeerInfo>>(IpcMethods.DiscoveryGetServicePeers) ?? [];
        await RefreshPeersTableAsync();

        // Capture source thumbnails asynchronously so startup remains instant
        _ = RefreshThumbnailsAndPushUiAsync(forceRefresh: false);

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
                        StartServerOnAllNetworks();
                    }
                    await RefreshShareStatusUiAsync();
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
                case "source_picker":
                    await HandleSourcePickerActionAsync(ev);
                    break;
                case "resolution":
                    Settings.Resolution = ResolutionPresets.Normalize(ev.Value?.GetString());
                    SaveSettings();
                    if (_sharingEnabled && Settings.CaptureSource == "fullscreen") StartServerOnAllNetworks();
                    await RefreshShareStatusUiAsync();
                    await PushMetaUpdateAsync();
                    break;
                case "bitrate_mbps":
                    if (ev.Value?.TryGetInt32(out int mbps) == true)
                    {
                        Settings.BitrateMbps = ShareSettings.NormalizeBitrate(mbps);
                    }
                    SaveSettings();
                    if (_sharingEnabled) StartServerOnAllNetworks();
                    await RefreshShareStatusUiAsync();
                    await PushMetaUpdateAsync();
                    break;
                case "fps":
                    if (ev.Value?.TryGetInt32(out int fps) == true)
                    {
                        Settings.Fps = ShareSettings.NormalizeFps(fps);
                    }
                    SaveSettings();
                    if (_sharingEnabled) StartServerOnAllNetworks();
                    await RefreshShareStatusUiAsync();
                    await PushMetaUpdateAsync();
                    break;
                case "share_audio":
                    Settings.ShareAudio = ev.Value?.ValueKind == JsonValueKind.True;
                    SaveSettings();
                    if (_sharingEnabled) StartServerOnAllNetworks();
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
                case "btn_toggle_share":
                    await ToggleSharingAsync();
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

    private static async Task HandleSourcePickerActionAsync(EventUiActionParams ev)
    {
        switch (ev.Action)
        {
            case "tab":
            {
                string nextTab = ev.Value?.GetString() == "fullscreen" ? "fullscreen" : "window";
                if (Settings.CaptureSource == nextTab) return;

                Settings.CaptureSource = nextTab;
                _statusOverride = null;
                RefreshSourceListsMetadata();
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
                        StartServerOnAllNetworks();
                    }
                }

                await RebuildMainSchemaAsync();
                await PushMetaUpdateAsync();
                _ = RefreshThumbnailsAndPushUiAsync(forceRefresh: false);
                break;
            }
            case "select":
            {
                string? cardId = ev.Value?.GetString();
                if (string.IsNullOrWhiteSpace(cardId)) return;

                _statusOverride = null;
                if (cardId.StartsWith("win:", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(cardId[4..], out long hwndVal) &&
                    hwndVal > 0)
                {
                    Settings.CaptureSource = "window";
                    Settings.TargetWindowHwnd = new IntPtr(hwndVal);
                    var matched = _capturableWindows.FirstOrDefault(w => w.Hwnd == hwndVal);
                    if (matched is not null) Settings.LastWindowTitle = matched.Title;
                }
                else if (cardId.StartsWith("mon:", StringComparison.OrdinalIgnoreCase) &&
                         long.TryParse(cardId[4..], out long hmonVal) &&
                         hmonVal > 0)
                {
                    Settings.CaptureSource = "fullscreen";
                    Settings.TargetMonitorHandle = new IntPtr(hmonVal);
                    var matched = _capturableMonitors.FirstOrDefault(m => m.HMonitor == hmonVal);
                    if (matched is not null) Settings.TargetMonitorDevice = matched.DeviceName;
                }

                SaveSettings();
                if (_sharingEnabled)
                {
                    StartServerOnAllNetworks();
                }
                await RefreshShareStatusUiAsync();
                await PushMetaUpdateAsync();
                break;
            }
            case "refresh":
                _statusOverride = null;
                await RefreshThumbnailsAndPushUiAsync(forceRefresh: true);
                break;
        }
    }

    private static async Task ToggleSharingAsync()
    {
        _statusOverride = null;
        if (!_sharingEnabled)
        {
            if (Settings.CaptureSource == "window")
            {
                RefreshSourceListsMetadata();
                if (!WindowEnumerator.IsValidWindow(Settings.TargetWindowHwnd))
                {
                    _sharingEnabled = false;
                    _statusOverride = "请先在左侧选择要共享的程序窗口";
                    await RefreshShareStatusUiAsync();
                    _ = RefreshThumbnailsAndPushUiAsync(forceRefresh: true);
                    return;
                }
            }

            _sharingEnabled = true;
            StartServerOnAllNetworks();
        }
        else
        {
            _sharingEnabled = false;
            _server?.Stop();
        }

        await RefreshShareStatusUiAsync();
        await PushMetaUpdateAsync();
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
            _ = RefreshThumbnailsAndPushUiAsync(forceRefresh: false);
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

    private static void StartServerOnAllNetworks()
    {
        if (_server is null) return;
        var ips = _networks
            .Where(n => n.Running && !string.IsNullOrWhiteSpace(n.Ipv4))
            .Select(n => n.Ipv4)
            .ToList();
        _server.Start(ips);
    }

    private static async Task PushMetaUpdateAsync()
    {
        if (_ipc is null) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.DiscoveryUpdateMeta, new DiscoveryUpdateMetaParams
        {
            Meta = BuildMetaElement(),
            NetworkIds = null
        });
    }

    private static async Task HandleCaptureTargetClosedAsync()
    {
        _sharingEnabled = false;
        _statusOverride = "共享窗口已关闭，已自动停止共享";
        RefreshSourceListsMetadata();
        await RebuildMainSchemaAsync();
        await PushMetaUpdateAsync();
        _ = RefreshThumbnailsAndPushUiAsync(forceRefresh: true);
    }

    private static void RefreshSourceListsMetadata()
    {
        _capturableWindows = WindowEnumerator.GetCapturableWindows();
        if (_capturableWindows.Count == 0)
        {
            Settings.TargetWindowHwnd = IntPtr.Zero;
        }
        else
        {
            var byHwnd = Settings.TargetWindowHwnd != IntPtr.Zero
                ? _capturableWindows.FirstOrDefault(w => w.Hwnd == Settings.TargetWindowHwnd.ToInt64())
                : null;
            if (byHwnd is not null)
            {
                Settings.LastWindowTitle = byHwnd.Title;
            }
            else
            {
                var byTitle = !string.IsNullOrWhiteSpace(Settings.LastWindowTitle)
                    ? _capturableWindows.FirstOrDefault(w => string.Equals(w.Title, Settings.LastWindowTitle, StringComparison.OrdinalIgnoreCase))
                    : null;
                var chosen = byTitle ?? _capturableWindows[0];
                Settings.TargetWindowHwnd = new IntPtr(chosen.Hwnd);
                Settings.LastWindowTitle = chosen.Title;
            }
        }

        _capturableMonitors = WindowEnumerator.GetCapturableMonitors();
        if (_capturableMonitors.Count == 0)
        {
            Settings.TargetMonitorHandle = IntPtr.Zero;
        }
        else
        {
            var byHandle = Settings.TargetMonitorHandle != IntPtr.Zero
                ? _capturableMonitors.FirstOrDefault(m => m.HMonitor == Settings.TargetMonitorHandle.ToInt64())
                : null;
            var byDevice = byHandle ?? (!string.IsNullOrWhiteSpace(Settings.TargetMonitorDevice)
                ? _capturableMonitors.FirstOrDefault(m => string.Equals(m.DeviceName, Settings.TargetMonitorDevice, StringComparison.OrdinalIgnoreCase))
                : null);
            var chosenMon = byDevice ?? _capturableMonitors[0];
            Settings.TargetMonitorHandle = new IntPtr(chosenMon.HMonitor);
            Settings.TargetMonitorDevice = chosenMon.DeviceName;
        }
    }

    private static async Task RefreshThumbnailsAndPushUiAsync(bool forceRefresh)
    {
        if (_ipc is null || _inViewerMode) return;
        await ThumbnailRefreshLock.WaitAsync();
        try
        {
            if (forceRefresh)
            {
                ThumbnailCache.Clear();
            }

            RefreshSourceListsMetadata();

            if (Settings.CaptureSource == "window")
            {
                var snapshot = _capturableWindows.Take(16).ToList();
                foreach (var w in snapshot)
                {
                    string key = $"win:{w.Hwnd}";
                    if (!forceRefresh && ThumbnailCache.ContainsKey(key)) continue;
                    ThumbnailCache[key] = await ThumbnailCapturer.CaptureWindowJpegBase64Async(new IntPtr(w.Hwnd));
                }
            }
            else
            {
                foreach (var m in _capturableMonitors)
                {
                    string key = $"mon:{m.HMonitor}";
                    if (!forceRefresh && ThumbnailCache.ContainsKey(key)) continue;
                    ThumbnailCache[key] = await ThumbnailCapturer.CaptureMonitorJpegBase64Async(m);
                }
            }

            if (_ipc is null || _inViewerMode) return;
            await _ipc.SendRequestAsync<object>(IpcMethods.UiUpdateValues, new UiUpdateValuesParams
            {
                MenuId = MainMenuId,
                Values = new Dictionary<string, JsonElement>
                {
                    ["source_picker"] = JsonSerializer.SerializeToElement(new UiSourcePickerState
                    {
                        ActiveTab = Settings.CaptureSource,
                        SelectedId = SelectedSourceCardId(),
                        Cards = BuildCurrentSourceCards()
                    })
                }
            });
        }
        catch
        {
        }
        finally
        {
            ThumbnailRefreshLock.Release();
        }
    }

    private static string SelectedSourceCardId() => Settings.CaptureSource == "fullscreen"
        ? $"mon:{Settings.TargetMonitorHandle.ToInt64()}"
        : $"win:{Settings.TargetWindowHwnd.ToInt64()}";

    private static List<UiSourceCard> BuildCurrentSourceCards()
    {
        if (Settings.CaptureSource == "fullscreen")
        {
            return _capturableMonitors.Select(m =>
            {
                string id = $"mon:{m.HMonitor}";
                ThumbnailCache.TryGetValue(id, out var thumb);
                return new UiSourceCard
                {
                    Id = id,
                    Title = m.DisplayLabel,
                    Subtitle = m.DeviceName,
                    ThumbnailBase64 = thumb
                };
            }).ToList();
        }

        return _capturableWindows.Take(16).Select(w =>
        {
            string id = $"win:{w.Hwnd}";
            ThumbnailCache.TryGetValue(id, out var thumb);
            return new UiSourceCard
            {
                Id = id,
                Title = w.Title,
                Subtitle = $"进程: {w.ProcessName}",
                ThumbnailBase64 = thumb
            };
        }).ToList();
    }

    private static JsonElement BuildMetaElement()
    {
        var spec = Settings.CaptureSource == "window"
            ? $"程序@{Settings.Fps}"
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

    private static string BuildShareStatusText()
    {
        if (!string.IsNullOrWhiteSpace(_statusOverride))
        {
            return _statusOverride!;
        }

        int runningNets = _networks.Count(n => n.Running);
        if (_sharingEnabled)
        {
            int viewers = _server?.ActiveViewerCount ?? 0;
            return $"共享中 · {runningNets} 个虚拟网络在线 · 观众 {viewers} 人";
        }

        return runningNets > 0
            ? $"未开启 · 就绪（生效到全部 {runningNets} 个在线网络）"
            : "未开启 · 暂无在线虚拟网络";
    }

    private static async Task RefreshShareStatusUiAsync()
    {
        if (_ipc is null || _inViewerMode) return;
        await _ipc.SendRequestAsync<object>(IpcMethods.UiUpdateValues, new UiUpdateValuesParams
        {
            MenuId = MainMenuId,
            Values = new Dictionary<string, JsonElement>
            {
                ["share_status"] = JsonSerializer.SerializeToElement(BuildShareStatusText()),
                ["btn_toggle_share"] = JsonSerializer.SerializeToElement(new
                {
                    label = _sharingEnabled ? "停止共享" : "开始共享",
                    danger = _sharingEnabled
                })
            }
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

    private static async Task RebuildMainSchemaAsync()
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
        var rightItems = new List<UiItem>();

        if (Settings.CaptureSource == "fullscreen")
        {
            rightItems.Add(new UiItem
            {
                Key = "resolution",
                Type = "select",
                Column = "right",
                Stacked = true,
                Label = "分辨率",
                Value = JsonSerializer.SerializeToElement(ResolutionPresets.Normalize(Settings.Resolution)),
                Options = ResolutionPresets.All.Select(r => new UiSelectOption
                {
                    Label = r.Label,
                    Value = JsonSerializer.SerializeToElement(r.Key)
                }).ToList()
            });
        }

        rightItems.Add(new UiItem
        {
            Key = "bitrate_mbps",
            Type = "segmented",
            Column = "right",
            Stacked = true,
            Label = "画质",
            Subtitle = "同画质下，帧率越低单帧画面越清晰",
            Value = JsonSerializer.SerializeToElement(ShareSettings.NormalizeBitrate(Settings.BitrateMbps)),
            Options =
            [
                new() { Label = "2 Mbps", Value = JsonSerializer.SerializeToElement(2) },
                new() { Label = "4 Mbps", Value = JsonSerializer.SerializeToElement(4) },
                new() { Label = "6 Mbps", Value = JsonSerializer.SerializeToElement(6) }
            ]
        });

        rightItems.Add(new UiItem
        {
            Key = "fps",
            Type = "segmented",
            Column = "right",
            Stacked = true,
            Label = "帧率",
            Value = JsonSerializer.SerializeToElement(ShareSettings.NormalizeFps(Settings.Fps)),
            Options =
            [
                new() { Label = "15 FPS", Value = JsonSerializer.SerializeToElement(15) },
                new() { Label = "30 FPS", Value = JsonSerializer.SerializeToElement(30) },
                new() { Label = "60 FPS", Value = JsonSerializer.SerializeToElement(60) }
            ]
        });

        rightItems.Add(new UiItem
        {
            Key = "share_audio",
            Type = "switch",
            Column = "right",
            Label = "共享声音",
            Value = JsonSerializer.SerializeToElement(Settings.ShareAudio)
        });

        rightItems.Add(new UiItem
        {
            Key = "allow_control",
            Type = "switch",
            Column = "right",
            Label = "允许远控",
            Value = JsonSerializer.SerializeToElement(Settings.AllowRemoteControl)
        });

        rightItems.Add(new UiItem
        {
            Key = "share_status",
            Type = "status",
            Column = "right",
            Label = "当前状态",
            Value = JsonSerializer.SerializeToElement(BuildShareStatusText())
        });

        rightItems.Add(new UiItem
        {
            Key = "btn_toggle_share",
            Type = "button",
            Column = "right",
            Primary = true,
            Danger = _sharingEnabled,
            Label = _sharingEnabled ? "停止共享" : "开始共享"
        });

        var shareItems = new List<UiItem>
        {
            new()
            {
                Key = "source_picker",
                Type = "source_picker",
                Column = "left",
                ActiveTab = Settings.CaptureSource,
                Value = JsonSerializer.SerializeToElement(SelectedSourceCardId()),
                Tabs =
                [
                    new() { Label = "程序", Value = JsonSerializer.SerializeToElement("window") },
                    new() { Label = "屏幕", Value = JsonSerializer.SerializeToElement("fullscreen") }
                ],
                Cards = BuildCurrentSourceCards()
            }
        };
        shareItems.AddRange(rightItems);

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
                    Id = "screen_share",
                    Title = "屏幕共享",
                    Layout = "split",
                    Items = shareItems
                }
            ]
        };
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
                loaded.CaptureSource = loaded.CaptureSource == "fullscreen" ? "fullscreen" : "window";
                loaded.Fps = ShareSettings.NormalizeFps(loaded.Fps);
                loaded.BitrateMbps = ShareSettings.NormalizeBitrate(loaded.BitrateMbps);
                loaded.TargetNetworkId = "__all__";
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
