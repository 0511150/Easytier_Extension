using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyTierExt.Contracts.Models;

namespace EasyTierExt.HostCore.Discovery;

public sealed class DiscoveryService : IAsyncDisposable
{
    public const int DefaultDiscoveryPort = 39527;

    private readonly Func<List<HostNetworkStatus>> _networksProvider;
    private readonly Func<string?, List<HostPeerInfo>> _peersProvider;
    private readonly int _listenPort;
    private readonly ConcurrentDictionary<string, RegisteredPluginService> _localServices = new();
    private readonly ConcurrentDictionary<string, NetworkUdpListener> _listeners = new();
    private readonly ConcurrentDictionary<string, RemotePluginEntry> _remoteEntries = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int? _targetPortOverride;
    private Task? _loopTask;

    public event Action<string, List<ServicePeerInfo>>? DiscoveryUpdated;

    public DiscoveryService(
        Func<List<HostNetworkStatus>> networksProvider,
        Func<string?, List<HostPeerInfo>> peersProvider,
        int listenPort = DefaultDiscoveryPort,
        int? targetPortOverride = null)
    {
        _networksProvider = networksProvider;
        _peersProvider = peersProvider;
        _listenPort = listenPort;
        _targetPortOverride = targetPortOverride;
    }

    public void Start()
    {
        _loopTask ??= Task.Run(ProbeAndPruneLoopAsync);
    }

    public void RegisterService(string pluginId, int port, string proto, JsonElement meta, List<string>? networkIds)
    {
        _localServices[pluginId] = new RegisteredPluginService(pluginId, port, proto, meta.Clone(), networkIds);
        _ = BroadcastOnceAsync();
    }

    public void UpdateMeta(string pluginId, JsonElement meta, List<string>? networkIds)
    {
        if (_localServices.TryGetValue(pluginId, out var existing))
        {
            _localServices[pluginId] = existing with
            {
                Meta = meta.Clone(),
                NetworkIds = networkIds ?? existing.NetworkIds
            };
            _ = BroadcastOnceAsync();
        }
    }

    public void UnregisterPlugin(string pluginId)
    {
        _localServices.TryRemove(pluginId, out _);
    }

    public List<ServicePeerInfo> GetServicePeers(string pluginId, string? networkId = null)
    {
        var peers = _peersProvider(networkId).ToDictionary(
            p => $"{p.NetworkId}|{p.Ipv4}",
            p => p,
            StringComparer.Ordinal);
        var networks = _networksProvider().ToDictionary(n => n.NetworkId, n => n.NetworkName, StringComparer.Ordinal);

        var result = new List<ServicePeerInfo>();
        foreach (var entry in _remoteEntries.Values)
        {
            if (!string.Equals(entry.PluginId, pluginId, StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(networkId) && !string.Equals(entry.NetworkId, networkId, StringComparison.Ordinal))
            {
                continue;
            }

            peers.TryGetValue($"{entry.NetworkId}|{entry.Ipv4}", out var livePeer);
            networks.TryGetValue(entry.NetworkId, out var netName);

            result.Add(new ServicePeerInfo
            {
                NetworkId = entry.NetworkId,
                NetworkName = netName ?? entry.NetworkId,
                Ipv4 = entry.Ipv4,
                Hostname = livePeer?.Hostname ?? entry.Hostname,
                RouteType = livePeer?.RouteType ?? "relay",
                LatencyMs = livePeer?.LatencyMs ?? "-",
                Port = entry.Port,
                Proto = entry.Proto,
                Meta = entry.Meta.Clone()
            });
        }
        return result;
    }

    public async Task BroadcastOnceAsync()
    {
        SyncListeners();
        var networks = _networksProvider();
        foreach (var net in networks)
        {
            if (!net.Running || string.IsNullOrWhiteSpace(net.Ipv4))
            {
                continue;
            }
            if (!_listeners.TryGetValue(net.NetworkId, out var listener))
            {
                continue;
            }

            var packet = BuildPacket("announce", net);
            if (packet is null)
            {
                continue;
            }

            var peers = _peersProvider(net.NetworkId);
            int targetPort = _targetPortOverride ?? _listenPort;
            foreach (var peer in peers)
            {
                if (!IPAddress.TryParse(peer.Ipv4, out var peerIp))
                {
                    continue;
                }
                try
                {
                    await listener.Client.SendAsync(packet, packet.Length, new IPEndPoint(peerIp, targetPort));
                }
                catch
                {
                }
            }
        }
    }

    private async Task ProbeAndPruneLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await BroadcastOnceAsync();
                PruneExpired();
            }
            catch
            {
            }

            try
            {
                await Task.Delay(3000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SyncListeners()
    {
        var active = _networksProvider()
            .Where(n => n.Running && IPAddress.TryParse(n.Ipv4, out _))
            .ToDictionary(n => n.NetworkId, n => n);

        foreach (var key in _listeners.Keys.ToList())
        {
            if (!active.TryGetValue(key, out var net) || _listeners[key].BoundIp != net.Ipv4)
            {
                if (_listeners.TryRemove(key, out var old))
                {
                    old.Dispose();
                }
            }
        }

        foreach (var (netId, net) in active)
        {
            if (_listeners.ContainsKey(netId))
            {
                continue;
            }

            if (!IPAddress.TryParse(net.Ipv4, out var ip))
            {
                continue;
            }

            try
            {
                var udp = new UdpClient(new IPEndPoint(ip, _listenPort));
                var wrapper = new NetworkUdpListener(netId, net.Ipv4, udp);
                if (_listeners.TryAdd(netId, wrapper))
                {
                    _ = Task.Run(() => ReceiveLoopAsync(wrapper));
                }
                else
                {
                    wrapper.Dispose();
                }
            }
            catch
            {
            }
        }
    }

    private async Task ReceiveLoopAsync(NetworkUdpListener listener)
    {
        while (!_cts.IsCancellationRequested && !listener.IsDisposed)
        {
            try
            {
                var result = await listener.Client.ReceiveAsync(_cts.Token);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(json);
                if (packet is null || packet.Magic != "ETEXT1")
                {
                    continue;
                }

                var senderIp = result.RemoteEndPoint.Address.ToString();
                var affectedPlugins = new HashSet<string>(StringComparer.Ordinal);
                foreach (var svc in packet.Services)
                {
                    var key = $"{packet.NetworkId}|{senderIp}|{svc.PluginId}";
                    _remoteEntries[key] = new RemotePluginEntry(
                        packet.NetworkId,
                        senderIp,
                        packet.Hostname,
                        svc.PluginId,
                        svc.Port,
                        svc.Proto,
                        svc.Meta.Clone(),
                        DateTime.UtcNow);
                    affectedPlugins.Add(svc.PluginId);
                }

                foreach (var pluginId in affectedPlugins)
                {
                    DiscoveryUpdated?.Invoke(pluginId, GetServicePeers(pluginId));
                }

                if (packet.Type == "announce")
                {
                    var net = _networksProvider().FirstOrDefault(n => n.NetworkId == listener.NetworkId);
                    if (net is not null)
                    {
                        var ack = BuildPacket("ack", net);
                        if (ack is not null)
                        {
                            await listener.Client.SendAsync(ack, ack.Length, result.RemoteEndPoint);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (listener.IsDisposed) break;
            }
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-12);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, entry) in _remoteEntries)
        {
            if (entry.LastSeenUtc < cutoff && _remoteEntries.TryRemove(key, out _))
            {
                affected.Add(entry.PluginId);
            }
        }

        foreach (var pluginId in affected)
        {
            DiscoveryUpdated?.Invoke(pluginId, GetServicePeers(pluginId));
        }
    }

    private byte[]? BuildPacket(string type, HostNetworkStatus net)
    {
        var services = new List<DiscoveryServiceItem>();
        foreach (var svc in _localServices.Values)
        {
            if (svc.NetworkIds is { Count: > 0 } && !svc.NetworkIds.Contains(net.NetworkId))
            {
                continue;
            }
            services.Add(new DiscoveryServiceItem
            {
                PluginId = svc.PluginId,
                Port = svc.Port,
                Proto = svc.Proto,
                Meta = svc.Meta
            });
        }

        if (services.Count == 0 && type == "announce")
        {
            return null;
        }

        var packet = new DiscoveryPacket
        {
            Magic = "ETEXT1",
            Type = type,
            NetworkId = net.NetworkId,
            Hostname = net.Hostname,
            Ipv4 = net.Ipv4,
            Services = services
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var l in _listeners.Values)
        {
            l.Dispose();
        }
        _listeners.Clear();
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        _cts.Dispose();
    }

    private sealed record RegisteredPluginService(string PluginId, int Port, string Proto, JsonElement Meta, List<string>? NetworkIds);
    private sealed record RemotePluginEntry(string NetworkId, string Ipv4, string Hostname, string PluginId, int Port, string Proto, JsonElement Meta, DateTime LastSeenUtc);

    private sealed class NetworkUdpListener(string networkId, string boundIp, UdpClient client) : IDisposable
    {
        public string NetworkId { get; } = networkId;
        public string BoundIp { get; } = boundIp;
        public UdpClient Client { get; } = client;
        public bool IsDisposed { get; private set; }
        public void Dispose()
        {
            IsDisposed = true;
            Client.Dispose();
        }
    }

    private sealed class DiscoveryPacket
    {
        [JsonPropertyName("magic")] public string Magic { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("network_id")] public string NetworkId { get; set; } = string.Empty;
        [JsonPropertyName("hostname")] public string Hostname { get; set; } = string.Empty;
        [JsonPropertyName("ipv4")] public string Ipv4 { get; set; } = string.Empty;
        [JsonPropertyName("services")] public List<DiscoveryServiceItem> Services { get; set; } = [];
    }

    private sealed class DiscoveryServiceItem
    {
        [JsonPropertyName("plugin_id")] public string PluginId { get; set; } = string.Empty;
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("proto")] public string Proto { get; set; } = "tcp";
        [JsonPropertyName("meta")] public JsonElement Meta { get; set; }
    }
}
