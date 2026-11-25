using System.Runtime.CompilerServices;
using EasyTierExt.HostCore.Configuration;
using EasyTierExt.HostCore.Core;
using EasyTierExt.HostCore.Native;
using Xunit;

namespace EasyTierExt.Tests;

public class EasyTierCliIntegrationTests
{
    private static bool HasCoreBinaries(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) &&
        File.Exists(Path.Combine(dir, "easytier-core.exe")) &&
        File.Exists(Path.Combine(dir, "easytier-cli.exe"));

    private static string ResolveCoreDirectory([CallerFilePath] string callerPath = "")
    {
        var envCore = Environment.GetEnvironmentVariable("EASYTIER_CORE_SRC");
        if (HasCoreBinaries(envCore))
        {
            return envCore!;
        }

        var testsDir = Path.GetDirectoryName(callerPath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
        var distCore = Path.Combine(repoRoot, "dist", "core");
        if (HasCoreBinaries(distCore))
        {
            return distCore;
        }

        return @"C:\Users\admin\AppData\Local\Temp\claude\3b77ca5fb4cc6fe1\5c502608-3145-4885-b6e9-2c94e0df5bb4\scratchpad\root1\core";
    }

    private static NetworkProfile CreateProfile(string id) => new()
    {
        NetworkId = id,
        DisplayName = id,
        ConfigToml = $$"""
            instance_name = "{{id}}"
            listeners = []

            [network_identity]
            network_name = "{{id}}"
            network_secret = "s${TEMP}"

            [flags]
            no_tun = true
            """
    };

    [Fact]
    public async Task CheckConfig_AcceptsGenerated_AndRejectsBrokenToml()
    {
        using var job = new JobObjectManager();
        await using var mgr = new EasyTierManager(Path.GetTempPath(), job, ResolveCoreDirectory());
        var profile = new NetworkProfile();
        NetworkConfigToml.EnsureGenerated(profile);
        Assert.True(await mgr.CheckConfigAsync(profile.ConfigToml));
        Assert.False(await mgr.CheckConfigAsync("dhcp = maybe"));
    }

    [Fact]
    public async Task MultiInstance_EasyTierCore_Runs_Concurrently_And_StopsCleanly()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"et_core_test_{Guid.NewGuid():N}");
        using var job = new JobObjectManager();
        await using var mgr = new EasyTierManager(Path.GetTempPath(), job, ResolveCoreDirectory(), dataRoot);
        Assert.True(mgr.IsCoreAvailable);

        List<NetworkProfile> profiles = [CreateProfile("tnet1"), CreateProfile("tnet2")];
        foreach (var p in profiles)
        {
            await mgr.StartNetworkAsync(p, () => profiles);
        }

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

        // 第二个实例的网卡名自动避让；密钥不展开环境变量；停止后运行时配置被清理
        Assert.Contains(mgr.GetLogs("tnet1"), l => l.Contains("网卡 EasyTier_With_Extension,"));
        Assert.Contains(mgr.GetLogs("tnet2"), l => l.Contains("网卡 EasyTier_With_Extension_2,"));
        Assert.Contains(mgr.GetLogs("tnet1"), l => l.Contains("dev_name = \"EasyTier_With_Extension\""));
        Assert.Contains(mgr.GetLogs("tnet1"), l => l.Contains("s${TEMP}"));
        Assert.Empty(Directory.GetFiles(Path.Combine(dataRoot, "config", "networks")));
        Directory.Delete(dataRoot, true);
    }
}
