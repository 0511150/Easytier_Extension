using EasyTierExt.HostCore.Configuration;
using EasyTierExt.HostCore.Core;
using EasyTierExt.HostCore.Native;
using Xunit;

namespace EasyTierExt.Tests;

public class EasyTierCliIntegrationTests
{
    private const string CoreSrc = @"C:\Users\admin\AppData\Local\Temp\claude\3b77ca5fb4cc6fe1\5c502608-3145-4885-b6e9-2c94e0df5bb4\scratchpad\root1\core";

    [Fact]
    public async Task MultiInstance_EasyTierCore_Runs_Concurrently_And_StopsCleanly()
    {
        using var job = new JobObjectManager();
        await using var mgr = new EasyTierManager(Path.GetTempPath(), job, CoreSrc);
        Assert.True(mgr.IsCoreAvailable);

        var p1 = new NetworkProfile { NetworkId = "tnet1", DisplayName = "T1", NetworkName = "tnet1", NetworkSecret = "s1", ConnectionMode = "standalone", Listeners = [], NoTun = true };
        var p2 = new NetworkProfile { NetworkId = "tnet2", DisplayName = "T2", NetworkName = "tnet2", NetworkSecret = "s2", ConnectionMode = "standalone", Listeners = [], NoTun = true };
        List<NetworkProfile> profiles = [p1, p2];

        await mgr.StartNetworkAsync(p1, () => profiles);
        await mgr.StartNetworkAsync(p2, () => profiles);

        await Task.Delay(2500);

        var statuses = mgr.GetNetworkStatuses(profiles);
        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, s => Assert.True(s.Running));

        var peersWithSelf = mgr.GetPeers("tnet1", includeSelf: true);
        Assert.NotEmpty(peersWithSelf);
        Assert.True(peersWithSelf[0].IsSelf);
        Assert.Empty(mgr.GetPeers("tnet1", includeSelf: false));

        await mgr.StopNetworkAsync("tnet1", () => profiles);
        await mgr.StopNetworkAsync("tnet2", () => profiles);

        var after = mgr.GetNetworkStatuses(profiles);
        Assert.All(after, s => Assert.False(s.Running));
    }
}
