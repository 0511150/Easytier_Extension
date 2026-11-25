using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Configuration;
using EasyTierExt.HostCore.Native;

namespace EasyTierExt.HostCore.Core;

public sealed class EasyTierManager : IAsyncDisposable
{
    private readonly string _coreExePath;
    private readonly string _cliExePath;
    private readonly string _runtimeConfigDir;
    private readonly JobObjectManager _jobObject;
    private readonly ConcurrentDictionary<string, RunningInstance> _instances = new();
    private readonly ConcurrentDictionary<string, Queue<string>> _logs = new();
    private readonly object _portLock = new();
    private int _nextRpcPort = 15890;
    private bool _disposed;

    public event Action<List<HostNetworkStatus>>? NetworksChanged;
    public event Action<string, List<HostPeerInfo>>? PeersUpdated;
    public event Action<string, string>? LogAppended;

    public EasyTierManager(string baseDirectory, JobObjectManager jobObject, string? coreDirOverride = null, string? dataRootDirectory = null)
    {
        var coreDir = coreDirOverride ?? Path.Combine(baseDirectory, "core");
        _coreExePath = Path.Combine(coreDir, "easytier-core.exe");
        _cliExePath = Path.Combine(coreDir, "easytier-cli.exe");
        _runtimeConfigDir = Path.Combine(dataRootDirectory ?? baseDirectory, "config", "networks");
        _jobObject = jobObject;
    }

    public bool IsCoreAvailable => File.Exists(_coreExePath) && File.Exists(_cliExePath);

    public IReadOnlyList<string> GetLogs(string networkId)
    {
        if (!_logs.TryGetValue(networkId, out var q))
        {
            return [];
        }
        lock (q)
        {
            return [.. q];
        }
    }

    public void ClearLogs(string networkId)
    {
        if (_logs.TryGetValue(networkId, out var q))
        {
            lock (q)
            {
                q.Clear();
            }
        }
    }

    public List<HostNetworkStatus> GetNetworkStatuses(IEnumerable<NetworkProfile> profiles)
    {
        var list = new List<HostNetworkStatus>();
        foreach (var profile in profiles)
        {
            if (_instances.TryGetValue(profile.NetworkId, out var inst) && !inst.Process.HasExited)
            {
                list.Add(inst.ToStatus());
            }
            else
            {
                list.Add(new HostNetworkStatus
                {
                    NetworkId = profile.NetworkId,
                    NetworkName = profile.NetworkName,
                    DisplayName = profile.DisplayName,
                    Running = false,
                    Hostname = profile.Hostname
                });
            }
        }
        return list;
    }

    public List<HostPeerInfo> GetPeers(string? networkId = null, bool includeSelf = false)
    {
        var result = new List<HostPeerInfo>();
        if (!string.IsNullOrWhiteSpace(networkId))
        {
            if (_instances.TryGetValue(networkId, out var inst))
            {
                result.AddRange(inst.GetPeersSnapshot().Where(p => includeSelf || !p.IsSelf));
            }
            return includeSelf ? [.. result.OrderByDescending(p => p.IsSelf)] : result;
        }

        foreach (var inst in _instances.Values)
        {
            result.AddRange(inst.GetPeersSnapshot().Where(p => includeSelf || !p.IsSelf));
        }
        return includeSelf ? [.. result.OrderByDescending(p => p.IsSelf)] : result;
    }

    public async Task StartNetworkAsync(NetworkProfile profile, Func<IReadOnlyList<NetworkProfile>> allProfilesProvider)
    {
        if (!IsCoreAvailable)
        {
            throw new FileNotFoundException($"找不到核心程序: {_coreExePath}");
        }

        await StopNetworkAsync(profile.NetworkId, allProfilesProvider);

        NetworkConfigToml.EnsureGenerated(profile);
        var rpcPort = AllocateFreeTcpPort();
        var devName = NetworkConfigToml.MakeUniqueDeviceName(
            NetworkConfigToml.GetDeviceName(profile.ConfigToml),
            [.. _instances.Values.Select(i => i.DevName)]);
        var configPath = WriteRuntimeConfig(profile);
        var psi = new ProcessStartInfo
        {
            FileName = _coreExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_coreExePath)!
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add($"127.0.0.1:{rpcPort}");
        psi.ArgumentList.Add("--dev-name");
        psi.ArgumentList.Add(devName);
        // 密钥等取值可能包含 $，不做环境变量展开
        psi.ArgumentList.Add("--disable-env-parsing");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
        {
            throw new InvalidOperationException($"无法启动网络 {profile.DisplayName}");
        }

        _jobObject.AssignProcess(proc);

        var cts = new CancellationTokenSource();
        var inst = new RunningInstance(profile, proc, rpcPort, devName, cts);
        _instances[profile.NetworkId] = inst;

        AppendLog(profile.NetworkId, $"[Host] 启动实例 {profile.DisplayName} (网卡 {devName}, RPC 127.0.0.1:{rpcPort}, PID={proc.Id})");

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                AppendLog(profile.NetworkId, e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                AppendLog(profile.NetworkId, $"[ERR] {e.Data}");
            }
        };
        proc.Exited += (_, _) =>
        {
            AppendLog(profile.NetworkId, $"[Host] 核心进程已退出 (Code={proc.ExitCode})");
            inst.Cts.Cancel();
            _instances.TryRemove(profile.NetworkId, out _);
            NetworksChanged?.Invoke(GetNetworkStatuses(allProfilesProvider()));
            PeersUpdated?.Invoke(profile.NetworkId, []);
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        _ = Task.Run(() => PollLoopAsync(inst, allProfilesProvider));
        NetworksChanged?.Invoke(GetNetworkStatuses(allProfilesProvider()));
    }

    public async Task StopNetworkAsync(string networkId, Func<IReadOnlyList<NetworkProfile>> allProfilesProvider)
    {
        // 核心只在启动时读取该文件，停止后不把密钥留在磁盘上
        DeleteRuntimeConfig(networkId);
        if (!_instances.TryRemove(networkId, out var inst))
        {
            return;
        }

        inst.Cts.Cancel();
        try
        {
            if (!inst.Process.HasExited)
            {
                inst.Process.Kill(entireProcessTree: true);
                await inst.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch
        {
        }
        finally
        {
            inst.Process.Dispose();
            inst.Cts.Dispose();
        }

        AppendLog(networkId, "[Host] 实例已停止");
        NetworksChanged?.Invoke(GetNetworkStatuses(allProfilesProvider()));
        PeersUpdated?.Invoke(networkId, []);
    }

    private async Task PollLoopAsync(RunningInstance inst, Func<IReadOnlyList<NetworkProfile>> allProfilesProvider)
    {
        while (!inst.Cts.IsCancellationRequested && !inst.Process.HasExited)
        {
            try
            {
                var nodeJson = await RunCliJsonAsync(inst.RpcPort, ["node", "info"], inst.Cts.Token);
                var peersJson = await RunCliJsonAsync(inst.RpcPort, ["peer", "list"], inst.Cts.Token);

                bool networkChanged = false;
                if (!string.IsNullOrWhiteSpace(nodeJson))
                {
                    var nodeInfo = JsonSerializer.Deserialize<CliNodeInfoDto>(nodeJson);
                    if (nodeInfo is not null)
                    {
                        var cidr = nodeInfo.Ipv4Addr ?? string.Empty;
                        var ipv4 = cidr.Contains('/') ? cidr.Split('/')[0] : cidr;
                        var host = string.IsNullOrWhiteSpace(nodeInfo.Hostname) ? inst.Profile.Hostname : nodeInfo.Hostname;
                        if (inst.Ipv4 != ipv4 || inst.Cidr != cidr || inst.Hostname != host)
                        {
                            inst.Ipv4 = ipv4;
                            inst.Cidr = cidr;
                            inst.Hostname = host;
                            networkChanged = true;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(peersJson))
                {
                    var rawPeers = JsonSerializer.Deserialize<List<CliPeerDto>>(peersJson) ?? [];
                    var parsedPeers = new List<HostPeerInfo>();
                    foreach (var p in rawPeers)
                    {
                        bool isSelf = string.Equals(p.Cost, "Local", StringComparison.OrdinalIgnoreCase);

                        var peerIpv4 = (p.Ipv4 ?? string.Empty).Split('/')[0];
                        if (isSelf && string.IsNullOrWhiteSpace(peerIpv4))
                        {
                            peerIpv4 = inst.Ipv4;
                        }

                        var hostName = string.IsNullOrWhiteSpace(p.Hostname)
                            ? (isSelf ? inst.Hostname : "-")
                            : p.Hostname;

                        var routeType = isSelf
                            ? "local"
                            : ((p.Cost ?? string.Empty).Contains("p2p", StringComparison.OrdinalIgnoreCase) ? "p2p" : "relay");

                        parsedPeers.Add(new HostPeerInfo
                        {
                            NetworkId = inst.Profile.NetworkId,
                            NetworkName = inst.Profile.NetworkName,
                            PeerId = p.Id ?? string.Empty,
                            Hostname = hostName,
                            Ipv4 = peerIpv4,
                            RouteType = routeType,
                            LatencyMs = p.LatMs ?? "-",
                            LossRate = p.LossRate ?? "-",
                            NatType = p.NatType ?? "-",
                            TunnelProto = p.TunnelProto ?? "-",
                            RxBytes = p.RxBytes ?? "-",
                            TxBytes = p.TxBytes ?? "-",
                            IsSelf = isSelf
                        });
                    }

                    if (!parsedPeers.Any(x => x.IsSelf))
                    {
                        parsedPeers.Insert(0, new HostPeerInfo
                        {
                            NetworkId = inst.Profile.NetworkId,
                            NetworkName = inst.Profile.NetworkName,
                            Hostname = inst.Hostname,
                            Ipv4 = inst.Ipv4,
                            RouteType = "local",
                            IsSelf = true
                        });
                    }

                    if (inst.UpdatePeers(parsedPeers, out bool peerCountChanged))
                    {
                        if (peerCountChanged)
                        {
                            networkChanged = true;
                        }
                        PeersUpdated?.Invoke(inst.Profile.NetworkId, parsedPeers.Where(x => !x.IsSelf).ToList());
                    }
                }

                if (networkChanged)
                {
                    NetworksChanged?.Invoke(GetNetworkStatuses(allProfilesProvider()));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(2000, inst.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<string?> RunCliJsonAsync(int rpcPort, string[] subArgs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _cliExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add($"127.0.0.1:{rpcPort}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("json");
        foreach (var arg in subArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(3000);
        var output = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        await proc.WaitForExitAsync(timeoutCts.Token);
        return proc.ExitCode == 0 ? output : null;
    }

    private string WriteRuntimeConfig(NetworkProfile profile)
    {
        Directory.CreateDirectory(_runtimeConfigDir);
        var path = Path.Combine(_runtimeConfigDir, $"{profile.NetworkId}.toml");
        File.WriteAllText(path, profile.ConfigToml);
        return path;
    }

    private void DeleteRuntimeConfig(string networkId)
    {
        try { File.Delete(Path.Combine(_runtimeConfigDir, $"{networkId}.toml")); } catch { }
    }

    /// <summary>由核心检查配置语法；核心缺失时跳过。</summary>
    public async Task<bool> CheckConfigAsync(string toml)
    {
        if (!IsCoreAvailable) return true;

        Directory.CreateDirectory(_runtimeConfigDir);
        var path = Path.Combine(_runtimeConfigDir, $"check-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(path, toml);
            var psi = new ProcessStartInfo { FileName = _coreExePath, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("--disable-env-parsing");
            psi.ArgumentList.Add("--check-config");

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动核心检查配置");
            _jobObject.AssignProcess(proc);
            try
            {
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                proc.Kill(entireProcessTree: true);
                throw new TimeoutException("核心检查配置超时");
            }
            return proc.ExitCode == 0;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>没有虚拟 IP 的远端节点（公共/中继服务器）不在节点列表中展示。</summary>
    public static bool IsVisiblePeer(HostPeerInfo peer) =>
        peer.IsSelf || (!string.IsNullOrWhiteSpace(peer.Ipv4) && peer.Ipv4 is not ("-" or "0.0.0.0"));

    private int AllocateFreeTcpPort()
    {
        lock (_portLock)
        {
            for (int i = 0; i < 200; i++)
            {
                int candidate = _nextRpcPort++;
                if (_nextRpcPort > 19000) _nextRpcPort = 15890;
                try
                {
                    using var listener = new TcpListener(IPAddress.Loopback, candidate);
                    listener.Start();
                    listener.Stop();
                    return candidate;
                }
                catch
                {
                }
            }
            return 0;
        }
    }

    private void AppendLog(string networkId, string line)
    {
        var q = _logs.GetOrAdd(networkId, _ => new Queue<string>());
        var formatted = $"[{DateTime.Now:HH:mm:ss}] {line}";
        lock (q)
        {
            q.Enqueue(formatted);
            while (q.Count > 400)
            {
                q.Dequeue();
            }
        }
        LogAppended?.Invoke(networkId, formatted);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _instances.Keys.ToList())
        {
            await StopNetworkAsync(id, () => []);
        }
    }

    private sealed class RunningInstance
    {
        private readonly object _peerLock = new();
        private List<HostPeerInfo> _peers = [];
        private string _lastPeersSignature = string.Empty;
        private int _lastRemotePeerCount = -1;

        public RunningInstance(NetworkProfile profile, Process process, int rpcPort, string devName, CancellationTokenSource cts)
        {
            Profile = profile;
            Process = process;
            RpcPort = rpcPort;
            DevName = devName;
            Cts = cts;
            StartTimeUtc = DateTime.UtcNow;
            Hostname = profile.Hostname;
            _peers =
            [
                new HostPeerInfo
                {
                    NetworkId = profile.NetworkId,
                    NetworkName = profile.NetworkName,
                    Hostname = profile.Hostname,
                    Ipv4 = string.Empty,
                    RouteType = "local",
                    IsSelf = true
                }
            ];
        }

        public NetworkProfile Profile { get; }
        public Process Process { get; }
        public int RpcPort { get; }
        public string DevName { get; }
        public CancellationTokenSource Cts { get; }
        public DateTime StartTimeUtc { get; }
        public string Ipv4 { get; set; } = string.Empty;
        public string Cidr { get; set; } = string.Empty;
        public string Hostname { get; set; }

        public List<HostPeerInfo> GetPeersSnapshot()
        {
            lock (_peerLock)
            {
                return [.. _peers];
            }
        }

        public bool UpdatePeers(List<HostPeerInfo> peers, out bool peerCountChanged)
        {
            var sig = string.Join('|', peers.Select(p =>
                $"{p.IsSelf}:{p.PeerId}:{p.Hostname}:{p.Ipv4}:{p.RouteType}:{p.LatencyMs}:{p.LossRate}:{p.NatType}"));
            int remoteCount = peers.Count(p => !p.IsSelf);
            lock (_peerLock)
            {
                _peers = peers;
                peerCountChanged = remoteCount != _lastRemotePeerCount;
                _lastRemotePeerCount = remoteCount;

                if (sig == _lastPeersSignature)
                {
                    return false;
                }
                _lastPeersSignature = sig;
                return true;
            }
        }

        public HostNetworkStatus ToStatus()
        {
            int count;
            lock (_peerLock)
            {
                count = _peers.Count(p => !p.IsSelf);
            }
            return new HostNetworkStatus
            {
                NetworkId = Profile.NetworkId,
                NetworkName = Profile.NetworkName,
                DisplayName = Profile.DisplayName,
                Running = !Process.HasExited,
                Ipv4 = Ipv4,
                Cidr = Cidr,
                Hostname = Hostname,
                PeerCount = count,
                UptimeSec = (long)(DateTime.UtcNow - StartTimeUtc).TotalSeconds
            };
        }
    }

    private sealed class CliNodeInfoDto
    {
        [JsonPropertyName("ipv4_addr")]
        public string? Ipv4Addr { get; set; }

        [JsonPropertyName("hostname")]
        public string? Hostname { get; set; }
    }

    private sealed class CliPeerDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("ipv4")]
        public string? Ipv4 { get; set; }

        [JsonPropertyName("cidr")]
        public string? Cidr { get; set; }

        [JsonPropertyName("hostname")]
        public string? Hostname { get; set; }

        [JsonPropertyName("cost")]
        public string? Cost { get; set; }

        [JsonPropertyName("lat_ms")]
        public string? LatMs { get; set; }

        [JsonPropertyName("loss_rate")]
        public string? LossRate { get; set; }

        [JsonPropertyName("rx_bytes")]
        public string? RxBytes { get; set; }

        [JsonPropertyName("tx_bytes")]
        public string? TxBytes { get; set; }

        [JsonPropertyName("tunnel_proto")]
        public string? TunnelProto { get; set; }

        [JsonPropertyName("nat_type")]
        public string? NatType { get; set; }
    }
}
