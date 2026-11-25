using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Discovery;
using EasyTierExt.HostCore.Native;

namespace EasyTierExt.HostCore.Plugins;

public interface IHostUiBridge
{
    void OnPluginMenusChanged();
    void OnPluginSchemaUpdated(string pluginId, string menuId, UiPageSchema schema);
    void OnPluginValuesUpdated(string pluginId, string menuId, Dictionary<string, JsonElement> values);
    void OnPluginNavigateRequested(string pluginId, string menuId);
    Task<UiGetViewportHostResult> GetViewportHostAsync(string pluginId, string menuId);
}

public sealed class PluginManager : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly string _dataRootDirectory;
    private readonly JobObjectManager _jobObject;
    private readonly Func<List<HostNetworkStatus>> _networksProvider;
    private readonly Func<string?, List<HostPeerInfo>> _peersProvider;
    private readonly DiscoveryService _discovery;
    private readonly IHostUiBridge _uiBridge;

    private readonly ConcurrentDictionary<string, InstalledPluginInfo> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActivePluginRuntime> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _expectedTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    private Task? _pipeAcceptLoop;
    private bool _disposed;

    public PluginManager(
        JobObjectManager jobObject,
        Func<List<HostNetworkStatus>> networksProvider,
        Func<string?, List<HostPeerInfo>> peersProvider,
        DiscoveryService discovery,
        IHostUiBridge uiBridge,
        string? pipeNameOverride = null,
        string? dataRootDirectory = null)
    {
        _jobObject = jobObject;
        _networksProvider = networksProvider;
        _peersProvider = peersProvider;
        _discovery = discovery;
        _uiBridge = uiBridge;
        _pipeName = pipeNameOverride ?? $"easytier_ext_ipc_{Environment.ProcessId}";
        _dataRootDirectory = dataRootDirectory ?? EasyTierExt.HostCore.Configuration.ClientConfigStore.GetDefaultAppDataRoot();

        _discovery.DiscoveryUpdated += OnDiscoveryUpdated;
    }

    public string PipeFullName => $@"\\.\pipe\{_pipeName}";

    public void StartServer()
    {
        if (_pipeAcceptLoop is not null) return;
        var initialServer = CreateServerPipeInstance();
        _pipeAcceptLoop = Task.Run(() => AcceptPipeClientsLoopAsync(initialServer));
    }

    private NamedPipeServerStream CreateServerPipeInstance() =>
        new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    public void SetInstalledPlugins(IEnumerable<InstalledPluginInfo> plugins)
    {
        _installed.Clear();
        foreach (var p in plugins)
        {
            p.Running = _running.ContainsKey(p.Manifest.Id);
            _installed[p.Manifest.Id] = p;
        }
        _uiBridge.OnPluginMenusChanged();
    }

    public IReadOnlyList<InstalledPluginInfo> GetInstalledPlugins()
    {
        foreach (var p in _installed.Values)
        {
            p.Running = _running.ContainsKey(p.Manifest.Id);
        }
        return [.. _installed.Values.OrderBy(x => x.Manifest.Name, StringComparer.Ordinal)];
    }

    public IReadOnlyList<(string PluginId, PluginMenuSpec Menu)> GetActivePluginMenus()
    {
        var menus = new List<(string, PluginMenuSpec)>();
        foreach (var plugin in _installed.Values.Where(x => x.Enabled).OrderBy(x => x.Manifest.Name, StringComparer.Ordinal))
        {
            foreach (var menu in plugin.Manifest.Menus)
            {
                menus.Add((plugin.Manifest.Id, menu));
            }
        }
        return menus;
    }

    public UiPageSchema? GetMenuSchema(string pluginId, string menuId)
    {
        if (_running.TryGetValue(pluginId, out var rt) && rt.Schemas.TryGetValue(menuId, out var schema))
        {
            return schema;
        }
        return null;
    }

    public Dictionary<string, JsonElement>? GetMenuValues(string pluginId, string menuId)
    {
        if (_running.TryGetValue(pluginId, out var rt) && rt.Values.TryGetValue(menuId, out var vals))
        {
            return vals;
        }
        return null;
    }

    public async Task StartPluginAsync(string pluginId)
    {
        if (!_installed.TryGetValue(pluginId, out var info))
        {
            throw new KeyNotFoundException($"插件 {pluginId} 未安装。");
        }

        if (_running.ContainsKey(pluginId))
        {
            return;
        }

        var entryExe = Path.Combine(info.DirectoryPath, info.Manifest.Entry);
        if (!File.Exists(entryExe))
        {
            throw new FileNotFoundException($"插件入口程序不存在: {entryExe}");
        }

        var token = Guid.NewGuid().ToString("N");
        _expectedTokens[pluginId] = token;

        var psi = new ProcessStartInfo
        {
            FileName = entryExe,
            WorkingDirectory = info.DirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var pluginDataDir = Path.Combine(_dataRootDirectory, "plugins_data", pluginId);
        Directory.CreateDirectory(pluginDataDir);

        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(PipeFullName);
        psi.ArgumentList.Add("--plugin-id");
        psi.ArgumentList.Add(pluginId);
        psi.ArgumentList.Add("--token");
        psi.ArgumentList.Add(token);
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(pluginDataDir);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动插件 {pluginId}");
        _jobObject.AssignProcess(proc);

        var runtime = new ActivePluginRuntime(pluginId, proc);
        _running[pluginId] = runtime;
        info.Enabled = true;
        info.Running = true;

        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            _running.TryRemove(pluginId, out _);
            _discovery.UnregisterPlugin(pluginId);
            if (_installed.TryGetValue(pluginId, out var p))
            {
                p.Running = false;
            }
        };

        _uiBridge.OnPluginMenusChanged();
        await Task.CompletedTask;
    }

    public async Task StopPluginAsync(string pluginId)
    {
        _expectedTokens.TryRemove(pluginId, out _);
        _discovery.UnregisterPlugin(pluginId);

        if (_running.TryRemove(pluginId, out var rt))
        {
            if (rt.Connection is not null)
            {
                try
                {
                    await rt.Connection.SendNotificationAsync(IpcMethods.EventShutdown);
                }
                catch
                {
                }
            }

            if (rt.Process is not null && !rt.Process.HasExited)
            {
                try
                {
                    await rt.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(1500));
                }
                catch
                {
                }

                if (!rt.Process.HasExited)
                {
                    try { rt.Process.Kill(entireProcessTree: true); } catch { }
                }
                rt.Process.Dispose();
            }

            if (rt.Connection is not null)
            {
                await rt.Connection.DisposeAsync();
            }
        }

        if (_installed.TryGetValue(pluginId, out var info))
        {
            info.Running = false;
        }
        _uiBridge.OnPluginMenusChanged();
    }

    public void RegisterExpectedTokenForExternalClient(string pluginId, string token)
    {
        _expectedTokens[pluginId] = token;
    }

    public async Task SendUiActionAsync(string pluginId, EventUiActionParams action)
    {
        if (_running.TryGetValue(pluginId, out var rt) && rt.Connection is not null)
        {
            await rt.Connection.SendNotificationAsync(IpcMethods.EventUiAction, action);
        }
    }

    public async Task SendViewportChangedAsync(string pluginId, EventViewportChangedParams vp)
    {
        if (_running.TryGetValue(pluginId, out var rt) && rt.Connection is not null)
        {
            await rt.Connection.SendNotificationAsync(IpcMethods.EventViewportChanged, vp);
        }
    }

    public async Task BroadcastNetworksChangedAsync(List<HostNetworkStatus> networks)
    {
        var payload = new EventNetworksChangedParams { Networks = networks };
        foreach (var rt in _running.Values)
        {
            if (rt.Connection is not null)
            {
                try { await rt.Connection.SendNotificationAsync(IpcMethods.EventNetworksChanged, payload); } catch { }
            }
        }
    }

    public async Task BroadcastPeersUpdatedAsync(string networkId, List<HostPeerInfo> peers)
    {
        var payload = new EventPeersUpdatedParams { NetworkId = networkId, Peers = peers };
        foreach (var rt in _running.Values)
        {
            if (rt.Connection is not null)
            {
                try { await rt.Connection.SendNotificationAsync(IpcMethods.EventPeersUpdated, payload); } catch { }
            }
        }
    }

    private void OnDiscoveryUpdated(string pluginId, List<ServicePeerInfo> peers)
    {
        if (_running.TryGetValue(pluginId, out var rt) && rt.Connection is not null)
        {
            _ = rt.Connection.SendNotificationAsync(
                IpcMethods.EventDiscoveryUpdated,
                new EventDiscoveryUpdatedParams { ServicePeers = peers });
        }
    }

    private async Task AcceptPipeClientsLoopAsync(NamedPipeServerStream? server)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                server ??= CreateServerPipeInstance();
                await server.WaitForConnectionAsync(_cts.Token);
                var connectedStream = server;
                server = CreateServerPipeInstance();
                _ = Task.Run(() => HandlePipeClientAsync(connectedStream));
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                server = null;
                break;
            }
            catch
            {
                server?.Dispose();
                server = null;
                await Task.Delay(200);
            }
        }
        server?.Dispose();
    }

    private async Task HandlePipeClientAsync(NamedPipeServerStream stream)
    {
        var conn = new JsonRpcPipeConnection(stream);
        string? authenticatedPluginId = null;

        conn.Disconnected += () =>
        {
            if (authenticatedPluginId is not null &&
                _running.TryGetValue(authenticatedPluginId, out var rt) &&
                ReferenceEquals(rt.Connection, conn))
            {
                rt.Connection = null;
            }
            stream.Dispose();
        };

        conn.Start(async (method, @params) =>
        {
            if (method == IpcMethods.PluginHello)
            {
                var hello = @params?.Deserialize<PluginHelloParams>(IpcJsonContext.Options)
                    ?? throw new ArgumentException("无效的握手参数。");

                if (!_expectedTokens.TryGetValue(hello.PluginId, out var expected) ||
                    !string.Equals(expected, hello.Token, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("插件握手 Token 无效。");
                }

                authenticatedPluginId = hello.PluginId;
                var rt = _running.GetOrAdd(hello.PluginId, id => new ActivePluginRuntime(id, null));
                rt.Connection = conn;
                return new PluginHelloResult { HostVersion = Update.ClientUpdateService.GetCurrentVersion() };
            }

            if (authenticatedPluginId is null)
            {
                throw new UnauthorizedAccessException("请先完成 plugin.hello 握手。");
            }

            switch (method)
            {
                case IpcMethods.HostGetNetworks:
                    return _networksProvider();

                case IpcMethods.HostGetPeers:
                {
                    var req = @params?.Deserialize<GetPeersParams>(IpcJsonContext.Options);
                    return _peersProvider(req?.NetworkId);
                }

                case IpcMethods.DiscoveryRegister:
                {
                    var req = @params?.Deserialize<DiscoveryRegisterParams>(IpcJsonContext.Options)
                        ?? throw new ArgumentException("缺少注册参数。");
                    _discovery.RegisterService(authenticatedPluginId, req.Port, req.Proto, req.Meta, req.NetworkIds);
                    return new { ok = true };
                }

                case IpcMethods.DiscoveryUpdateMeta:
                {
                    var req = @params?.Deserialize<DiscoveryUpdateMetaParams>(IpcJsonContext.Options)
                        ?? throw new ArgumentException("缺少元数据参数。");
                    _discovery.UpdateMeta(authenticatedPluginId, req.Meta, req.NetworkIds);
                    return new { ok = true };
                }

                case IpcMethods.DiscoveryGetServicePeers:
                {
                    var req = @params?.Deserialize<GetPeersParams>(IpcJsonContext.Options);
                    return _discovery.GetServicePeers(authenticatedPluginId, req?.NetworkId);
                }

                case IpcMethods.UiSetSchema:
                {
                    var req = @params?.Deserialize<UiSetSchemaParams>(IpcJsonContext.Options)
                        ?? throw new ArgumentException("缺少 Schema 参数。");
                    if (_running.TryGetValue(authenticatedPluginId, out var rt))
                    {
                        rt.Schemas[req.MenuId] = req.Schema;
                    }
                    _uiBridge.OnPluginSchemaUpdated(authenticatedPluginId, req.MenuId, req.Schema);
                    return new { ok = true };
                }

                case IpcMethods.UiUpdateValues:
                {
                    var req = @params?.Deserialize<UiUpdateValuesParams>(IpcJsonContext.Options)
                        ?? throw new ArgumentException("缺少 Values 参数。");
                    if (_running.TryGetValue(authenticatedPluginId, out var rt))
                    {
                        var dict = rt.Values.GetOrAdd(req.MenuId, _ => new Dictionary<string, JsonElement>());
                        lock (dict)
                        {
                            foreach (var (k, v) in req.Values)
                            {
                                dict[k] = v.Clone();
                            }
                        }
                    }
                    _uiBridge.OnPluginValuesUpdated(authenticatedPluginId, req.MenuId, req.Values);
                    return new { ok = true };
                }

                case IpcMethods.UiNavigateTo:
                {
                    var req = @params?.Deserialize<UiNavigateToParams>(IpcJsonContext.Options)
                        ?? throw new ArgumentException("缺少目标菜单参数。");
                    _uiBridge.OnPluginNavigateRequested(authenticatedPluginId, req.MenuId);
                    return new { ok = true };
                }

                case IpcMethods.UiGetViewportHost:
                {
                    var req = @params?.Deserialize<UiGetViewportHostParams>(IpcJsonContext.Options)
                        ?? throw new ArgumentException("缺少菜单 ID。");
                    return await _uiBridge.GetViewportHostAsync(authenticatedPluginId, req.MenuId);
                }

                default:
                    throw new MissingMethodException($"未知的 IPC 方法: {method}");
            }
        },
        notificationHandler: null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _discovery.DiscoveryUpdated -= OnDiscoveryUpdated;
        _cts.Cancel();

        foreach (var id in _running.Keys.ToList())
        {
            await StopPluginAsync(id);
        }

        if (_pipeAcceptLoop is not null)
        {
            try { await _pipeAcceptLoop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        _cts.Dispose();
    }

    private sealed class ActivePluginRuntime(string pluginId, Process? process)
    {
        public string PluginId { get; } = pluginId;
        public Process? Process { get; } = process;
        public JsonRpcPipeConnection? Connection { get; set; }
        public ConcurrentDictionary<string, UiPageSchema> Schemas { get; } = new();
        public ConcurrentDictionary<string, Dictionary<string, JsonElement>> Values { get; } = new();
    }
}
