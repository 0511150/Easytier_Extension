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
    private readonly JobObjectManager _jobObject;
    private readonly ConcurrentDictionary<string, RunningInstance> _instances = new();
    private readonly ConcurrentDictionary<string, Queue<string>> _logs = new();
    private readonly object _portLock = new();
    private int _nextRpcPort = 15890;
    private bool _disposed;

    public event Action<List<HostNetworkStatus>>? NetworksChanged;
    public event Action<string, List<HostPeerInfo>>? PeersUpdated;
    public event Action<string, string>? LogAppended;

    public EasyTierManager(string baseDirectory, JobObjectManager jobObject, string? coreDirOverride = null)
    {
        var coreDir = coreDirOverride ?? Path.Combine(baseDirectory, "core");
        _coreExePath = Path.Combine(coreDir, "easytier-core.exe");
        _cliExePath = Path.Combine(coreDir, "easytier-cli.exe");
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

        var rpcPort = AllocateFreeTcpPort();
        var args = BuildCoreArguments(profile, rpcPort);
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
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
        {
            throw new InvalidOperationException($"无法启动网络 {profile.DisplayName}");
        }

        _jobObject.AssignProcess(proc);

        var cts = new CancellationTokenSource();
        var inst = new RunningInstance(profile, proc, rpcPort, cts);
        _instances[profile.NetworkId] = inst;

        AppendLog(profile.NetworkId, $"[Host] 启动实例 {profile.DisplayName} (RPC 127.0.0.1:{rpcPort}, PID={proc.Id})");

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

    private static List<string> BuildCoreArguments(NetworkProfile p, int rpcPort)
    {
        var args = new List<string>
        {
            "--instance-name", p.NetworkId,
            "--network-name", p.NetworkName,
            "--network-secret", p.NetworkSecret,
            "-r", $"127.0.0.1:{rpcPort}"
        };

        if (!string.IsNullOrWhiteSpace(p.Hostname))
        {
            args.Add("--hostname");
            args.Add(p.Hostname);
        }

        if (p.NoTun)
        {
            args.Add("--no-tun");
        }
        else if (string.Equals(p.Ipv4Mode, "static", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(p.StaticIpv4))
        {
            args.Add("-i");
            args.Add(p.StaticIpv4.Trim());
        }
        else
        {
            args.Add("-d");
        }

        if (string.Equals(p.ConnectionMode, "public", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(p.PublicServer))
        {
            args.Add("-p");
            args.Add(p.PublicServer.Trim());
        }
        else if (string.Equals(p.ConnectionMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var peer in p.PeerUrls.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                args.Add("-p");
                args.Add(peer.Trim());
            }
        }

        if (p.Listeners.Count == 0)
        {
            args.Add("--no-listener");
        }
        else
        {
            foreach (var l in p.Listeners.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                args.Add("-l");
                args.Add(l.Trim());
            }
        }

        foreach (var cidr in p.ProxyCidrs.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("-n");
            args.Add(cidr.Trim());
        }

        if (p.DisableEncryption)
        {
            args.Add("--disable-encryption");
            args.Add("true");
        }
        else if (!string.IsNullOrWhiteSpace(p.EncryptionAlgorithm))
        {
            args.Add("--encryption-algorithm");
            args.Add(p.EncryptionAlgorithm);
        }

        var devName = string.IsNullOrWhiteSpace(p.DevName) ? $"et_{p.NetworkId}" : p.DevName.Trim();
        args.Add("--dev-name");
        args.Add(devName);

        if (p.Mtu > 0)
        {
            args.Add("--mtu");
            args.Add(p.Mtu.ToString());
        }

        if (p.LatencyFirst)
        {
            args.Add("--latency-first");
            args.Add("true");
        }

        if (p.EnableKcpProxy)
        {
            args.Add("--enable-kcp-proxy");
            args.Add("true");
        }

        if (p.EnableQuicProxy)
        {
            args.Add("--enable-quic-proxy");
            args.Add("true");
        }

        return args;
    }

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

        public RunningInstance(NetworkProfile profile, Process process, int rpcPort, CancellationTokenSource cts)
        {
            Profile = profile;
            Process = process;
            RpcPort = rpcPort;
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
