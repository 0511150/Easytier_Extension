using System.Text.Json;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Discovery;
using Xunit;

namespace EasyTierExt.Tests;

public class DiscoveryServiceTests
{
    [Fact]
    public async Task TwoNodes_DiscoverEachOther_And_PropagateMeta()
    {
        int portA = 41521;
        int portB = 41522;

        var netsA = new List<HostNetworkStatus> { new() { NetworkId = "n1", NetworkName = "lan", Running = true, Ipv4 = "127.0.0.1", Hostname = "NodeA" } };
        var peersA = new List<HostPeerInfo> { new() { NetworkId = "n1", NetworkName = "lan", Ipv4 = "127.0.0.1", Hostname = "NodeB", RouteType = "p2p", LatencyMs = "1.2" } };

        var netsB = new List<HostNetworkStatus> { new() { NetworkId = "n1", NetworkName = "lan", Running = true, Ipv4 = "127.0.0.1", Hostname = "NodeB" } };
        var peersB = new List<HostPeerInfo> { new() { NetworkId = "n1", NetworkName = "lan", Ipv4 = "127.0.0.1", Hostname = "NodeA", RouteType = "p2p", LatencyMs = "1.2" } };

        await using var discA = new DiscoveryService(() => netsA, _ => peersA, listenPort: portA, targetPortOverride: portB);
        await using var discB = new DiscoveryService(() => netsB, _ => peersB, listenPort: portB, targetPortOverride: portA);

        var tcs = new TaskCompletionSource<List<ServicePeerInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        discA.DiscoveryUpdated += (pid, list) =>
        {
            if (pid == "screen-share" && list.Count > 0)
            {
                tcs.TrySetResult(list);
            }
        };

        discA.Start();
        discB.Start();

        discA.RegisterService("screen-share", 39600, "tcp", JsonSerializer.SerializeToElement(new { sharing = false }), null);
        discB.RegisterService("screen-share", 39600, "tcp", JsonSerializer.SerializeToElement(new { sharing = true }), null);

        await discB.BroadcastOnceAsync();
        var discovered = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(discovered);
        Assert.Equal("n1", discovered[0].NetworkId);
        Assert.True(discovered[0].Meta.GetProperty("sharing").GetBoolean());
    }
}
