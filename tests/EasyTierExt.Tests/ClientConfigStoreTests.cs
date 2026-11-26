using EasyTierExt.HostCore.Configuration;
using Xunit;

namespace EasyTierExt.Tests;

public class ClientConfigStoreTests
{
    [Fact]
    public void DefaultAppDataRoot_PointsTo_AppData_EasytierWithExtension()
    {
        var path = ClientConfigStore.GetDefaultAppDataRoot();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Easytier_With_Extension");
        Assert.Equal(expected, path);
    }

    [Fact]
    public void MigratesLegacyClientJson_And_SavesInTargetDataRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"et_cfg_test_{Guid.NewGuid():N}");
        var appDataDir = Path.Combine(tempRoot, "appdata");
        var legacyDir = Path.Combine(tempRoot, "legacy");
        Directory.CreateDirectory(Path.Combine(legacyDir, "config"));

        try
        {
            var legacyStore = new ClientConfigStore(legacyDir);
            var cfg = legacyStore.Load();
            var toml = cfg.Networks[0].ConfigToml;
            NetworkConfigToml.Store(cfg.Networks[0], NetworkConfigToml.Apply(toml, NetworkConfigToml.Read(toml) with { NetworkName = "migrated-net" }));
            legacyStore.Save(cfg);

            var newStore = new ClientConfigStore(appDataDir, legacyDir);
            var loaded = newStore.Load();
            Assert.Equal("migrated-net", loaded.Networks[0].NetworkName);
            Assert.True(File.Exists(Path.Combine(appDataDir, "config", "client.json")));

            var states = newStore.LoadPluginStates();
            Assert.True(states["screen-share"]);
            Assert.True(File.Exists(Path.Combine(appDataDir, "config", "plugins_state.json")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void DefaultNetworkProfile_UsesDhcpAnd_10_10_10_255_32_StaticIpv4()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"et_cfg_default_{Guid.NewGuid():N}");
        try
        {
            var store = new ClientConfigStore(tempRoot);
            var loaded = store.Load();
            var direct = new NetworkProfile();

            Assert.Equal("dhcp", direct.Ipv4Mode);
            Assert.Equal("10.10.10.255/32", direct.StaticIpv4);
            Assert.Single(loaded.Networks);
            Assert.Equal("dhcp", loaded.Networks[0].Ipv4Mode);
            Assert.Equal("10.10.10.255/32", loaded.Networks[0].StaticIpv4);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Load_ReplacesOldDefaultStaticIpv4_OnlyWhenStaticWasNeverUsed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"et_cfg_oldip_{Guid.NewGuid():N}");
        try
        {
            var store = new ClientConfigStore(tempRoot);
            var cfg = store.Load();
            cfg.Networks[0].StaticIpv4 = "10.10.10.10/24";
            var staticNet = new NetworkProfile { Ipv4Mode = "static", StaticIpv4 = "10.10.10.10/24" };
            cfg.Networks.Add(staticNet);
            store.Save(cfg);

            var loaded = new ClientConfigStore(tempRoot).Load();
            Assert.Equal("10.10.10.255/32", loaded.Networks[0].StaticIpv4);
            Assert.Equal("10.10.10.10/24", loaded.Networks[1].StaticIpv4);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Theory]
    [InlineData("10.10.10.255", "10.10.10.255/32", "10.10.10.255")]
    [InlineData(" 10.10.10.255/32 ", "10.10.10.255/32", "10.10.10.255")]
    [InlineData("10.10.10.8/24", "10.10.10.8/24", "10.10.10.8/24")]
    [InlineData("", "", "")]
    public void StaticIpv4_DefaultsTo32_AndHides32(string input, string normalized, string shown)
    {
        Assert.Equal(normalized, NetworkConfigToml.NormalizeIpv4(input));
        Assert.Equal(shown, NetworkConfigToml.DisplayIpv4(input));
    }
}
