using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Configuration;
using EasyTierExt.HostCore.Core;
using Xunit;

namespace EasyTierExt.Tests;

public class NetworkConfigTomlTests
{
    private const string Sample = """
        # 我的配置
        instance_name = "abc" # 行尾注释
        dhcp = true
        listeners = [
            "tcp://0.0.0.0:11010", # ] 干扰
        ]

        [network_identity]
        network_name = "net"
        network_secret = 'a"b\c'

        [[peer]]
        uri = "tcp://a:1"
        peer_public_key = "keep-me"

        [[peer]]
        uri = "tcp://b:2"

        [[proxy_network]]
        cidr = "192.168.1.0/24"

        [flags]
        mtu = 1300
        """;

    private static string N(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void NewProfile_GeneratesConfig_WithFixedTunName()
    {
        var p = new NetworkProfile();
        Assert.True(NetworkConfigToml.EnsureGenerated(p));

        Assert.Equal("EasyTier_With_Extension", NetworkConfigToml.GetDeviceName(p.ConfigToml));
        Assert.Contains("bind_device = true\nuse_smoltcp = true\nenable_udp_broadcast_relay = true", p.ConfigToml);
        var v = NetworkConfigToml.Read(p.ConfigToml);
        Assert.True(v.Dhcp);
        Assert.True(v.GameCompat);
        Assert.Equal([NetworkProfile.DefaultPeer], v.Peers);
        Assert.Equal(NetworkFormValues.ProtocolDefault, v.Protocol);
        Assert.False(NetworkConfigToml.EnsureGenerated(p));
    }

    [Fact]
    public void Read_HandlesCommentsLiteralsAndMultilineArrays()
    {
        var v = NetworkConfigToml.Read(Sample);
        Assert.Equal("net", v.NetworkName);
        Assert.Equal("a\"b\\c", v.NetworkSecret);
        Assert.Equal(["tcp://a:1", "tcp://b:2"], v.Peers);
        Assert.True(v.Dhcp);
    }

    [Fact]
    public void Apply_WithoutChanges_KeepsTextIdentical()
    {
        Assert.Equal(N(Sample), NetworkConfigToml.Apply(Sample, NetworkConfigToml.Read(Sample)));
    }

    [Fact]
    public void Apply_ChangesOnlyTouchedKeys()
    {
        var form = NetworkConfigToml.Read(Sample) with
        {
            NetworkSecret = "p\"w\\$1",
            Hostname = "主机",
            Dhcp = false,
            StaticIpv4 = "10.10.10.8/24",
            Peers = ["tcp://a:1", "udp://c:3"],
            Protocol = NetworkFormValues.ProtocolQuic
        };
        var result = NetworkConfigToml.Apply(Sample, form);

        Assert.True(form.SameAs(NetworkConfigToml.Read(result)));
        Assert.Contains("# 我的配置", result);
        Assert.Contains("# 行尾注释", result);
        Assert.Contains("peer_public_key = \"keep-me\"", result);
        Assert.Contains("[[proxy_network]]\ncidr = \"192.168.1.0/24\"", result);
        Assert.Contains("mtu = 1300\nenable_quic_proxy = true", result);
        Assert.DoesNotContain("tcp://b:2", result);

        // 切换回 KCP / Default 时不会残留另一个协议
        var kcp = NetworkConfigToml.Apply(result, form with { Protocol = NetworkFormValues.ProtocolKcp });
        Assert.Contains("enable_kcp_proxy = true", kcp);
        Assert.DoesNotContain("enable_quic_proxy", kcp);
        var def = NetworkConfigToml.Apply(kcp, form with { Protocol = NetworkFormValues.ProtocolDefault, Peers = [] });
        Assert.DoesNotContain("_proxy = true", def);
        Assert.DoesNotContain("[[peer]]", def);
    }

    [Fact]
    public void Apply_OnEmptyDocument_CreatesTables()
    {
        var form = new NetworkFormValues
        {
            NetworkName = "n", NetworkSecret = "s", Hostname = "h", Dhcp = true,
            Peers = ["tcp://a:1"], Protocol = NetworkFormValues.ProtocolKcp
        };
        Assert.True(form.SameAs(NetworkConfigToml.Read(NetworkConfigToml.Apply("", form))));
    }

    [Theory]
    [InlineData("a = 1\n[flags\n", "第 2 行")]
    [InlineData("hostname = \"x\n", "第 1 行")]
    [InlineData("dhcp = 1\n", "第 1 行")]
    [InlineData("a = 1\na = 2\n", "第 2 行")]
    [InlineData("flags = { mtu = 1 }\n", "[flags]")]
    [InlineData("listeners = [\"a\"\n", "第 1 行")]
    public void Read_ReportsErrors(string toml, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => NetworkConfigToml.Read(toml));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Store_AddsDeviceName_RefreshesProfile_AndRejectsBadIpv4()
    {
        var p = new NetworkProfile();
        NetworkConfigToml.Store(p, "ipv4 = \"10.1.2.3\"\n[network_identity]\nnetwork_name = \"pasted\"\nnetwork_secret = \"k\"\n");
        Assert.Contains("dev_name = \"EasyTier_With_Extension\"", p.ConfigToml);
        Assert.Equal("pasted", p.NetworkName);
        Assert.Equal("static", p.Ipv4Mode);

        Assert.Throws<FormatException>(() => NetworkConfigToml.Store(p, "ipv4 = \"abc\"\n"));
        Assert.Equal("pasted", p.NetworkName);
    }

    [Fact]
    public void GameCompat_DefaultsOn_AndExplicitOffIsKept()
    {
        // 上一版已生成、但没有这两个键的配置会被补全
        var p = new NetworkProfile { ConfigToml = "[flags]\nmtu = 1300\n" };
        Assert.True(NetworkConfigToml.EnsureGenerated(p));
        Assert.Contains("use_smoltcp = true", p.ConfigToml);
        Assert.Contains("bind_device = true", p.ConfigToml);
        Assert.Contains("enable_udp_broadcast_relay = true", p.ConfigToml);

        // 手动关闭的广播转发不会被重新打开
        var noRelay = new NetworkProfile { ConfigToml = "[flags]\nenable_udp_broadcast_relay = false\n" };
        NetworkConfigToml.EnsureGenerated(noRelay);
        Assert.Contains("enable_udp_broadcast_relay = false", noRelay.ConfigToml);
        Assert.DoesNotContain("enable_udp_broadcast_relay = true", noRelay.ConfigToml);

        var off = NetworkConfigToml.Apply(p.ConfigToml, NetworkConfigToml.Read(p.ConfigToml) with { GameCompat = false });
        NetworkConfigToml.Store(p, off);
        Assert.Contains("use_smoltcp = false", p.ConfigToml);
        Assert.False(NetworkConfigToml.EnsureGenerated(p));
        Assert.False(NetworkConfigToml.Read(p.ConfigToml).GameCompat);

        // 手动写了 bind_device = false 时视为关闭；重新开启会一并恢复
        var unbound = p.ConfigToml.Replace("use_smoltcp = false", "use_smoltcp = true").Replace("bind_device = true", "bind_device = false");
        var values = NetworkConfigToml.Read(unbound);
        Assert.False(values.GameCompat);
        var on = NetworkConfigToml.Apply(unbound, values with { GameCompat = true });
        Assert.Contains("use_smoltcp = true", on);
        Assert.Contains("bind_device = true", on);
    }

    [Fact]
    public void UniqueDeviceName_AppendsSuffixOnlyOnConflict()
    {
        Assert.Equal("X", NetworkConfigToml.MakeUniqueDeviceName("X", []));
        Assert.Equal("X_3", NetworkConfigToml.MakeUniqueDeviceName("X", ["x", "X_2"]));
    }

    [Fact]
    public void LegacyClientJson_MigratesAdvancedFieldsIntoToml()
    {
        var root = Path.Combine(Path.GetTempPath(), $"et_cfg_legacy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var path = Path.Combine(root, "config", "client.json");
            File.WriteAllText(path, """
                { "networks": [
                  { "network_id": "id1", "display_name": "MC", "network_name": "Minecraft", "network_secret": "p$w",
                    "connection_mode": "manual", "public_server": "tcp://pub:1", "peer_urls": ["wss://relay:443"],
                    "ipv4_mode": "static", "static_ipv4": "10.10.10.100/24", "hostname": "H", "auto_start": true,
                    "proxy_cidrs": ["192.168.10.0/24"], "listeners": ["tcp://0.0.0.0:12000"],
                    "disable_encryption": false, "encryption_algorithm": "chacha20", "dev_name": "old", "mtu": 1300,
                    "latency_first": true, "enable_kcp_proxy": false, "enable_quic_proxy": true, "no_tun": false },
                  { "network_id": "id2", "connection_mode": "public", "public_server": "tcp://pub:1", "peer_urls": [] },
                  { "network_id": "id3", "connection_mode": "standalone", "public_server": "tcp://pub:1", "no_tun": true }
                ] }
                """);

            var cfg = new ClientConfigStore(root).Load();
            var p = cfg.Networks[0];
            var v = NetworkConfigToml.Read(p.ConfigToml);
            Assert.Equal("Minecraft", v.NetworkName);
            Assert.Equal("p$w", v.NetworkSecret);
            Assert.Equal(["wss://relay:443"], v.Peers);
            Assert.Equal("10.10.10.100/24", v.StaticIpv4);
            Assert.Equal(NetworkFormValues.ProtocolQuic, v.Protocol);
            Assert.True(p.AutoStart);
            Assert.Equal("EasyTier_With_Extension", NetworkConfigToml.GetDeviceName(p.ConfigToml));
            foreach (var s in new[] { "192.168.10.0/24", "tcp://0.0.0.0:12000", "chacha20", "mtu = 1300", "latency_first = true" })
                Assert.Contains(s, p.ConfigToml);

            Assert.Equal(["tcp://pub:1"], NetworkConfigToml.Read(cfg.Networks[1].ConfigToml).Peers);
            Assert.Empty(NetworkConfigToml.Read(cfg.Networks[2].ConfigToml).Peers);
            Assert.Contains("no_tun = true", cfg.Networks[2].ConfigToml);

            var saved = File.ReadAllText(path);
            Assert.Contains("config_toml", saved);
            Assert.DoesNotContain("connection_mode", saved);
            Assert.Equal(p.ConfigToml, new ClientConfigStore(root).Load().Networks[0].ConfigToml);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NewProfile_DoesNotListen_SoSeveralNetworksCanRunTogether()
    {
        var p = new NetworkProfile();
        NetworkConfigToml.EnsureGenerated(p);
        Assert.Contains("listeners = []", p.ConfigToml);
        Assert.DoesNotContain("0.0.0.0:11010", p.ConfigToml);
    }

    [Theory]
    [InlineData("listeners = [\"tcp://0.0.0.0:11010\", \"udp://0.0.0.0:11010\"]\n", true)]
    [InlineData("listeners = [\n  \"udp://0.0.0.0:11010\", # 注释\n  \"tcp://0.0.0.0:11010\",\n]\n", true)]
    [InlineData("listeners = [\"tcp://0.0.0.0:11020\", \"udp://0.0.0.0:11020\"]\n", false)]
    [InlineData("listeners = [\"tcp://0.0.0.0:11010\"]\n", false)]
    [InlineData("listeners = []\n", false)]
    [InlineData("hostname = \"a\"\n", false)]
    public void LegacyDefaultListeners_AreDisabled_CustomOnesKept(string listenersLine, bool expectChanged)
    {
        var p = new NetworkProfile { ConfigToml = "instance_name = \"x\"\n" + listenersLine + "\n[network_identity]\nnetwork_name = \"n\"\n" };
        var before = p.ConfigToml;
        Assert.Equal(expectChanged, NetworkConfigToml.DisableLegacyDefaultListeners(p));
        if (expectChanged)
        {
            Assert.Contains("listeners = []", p.ConfigToml);
            Assert.DoesNotContain("0.0.0.0:11010", p.ConfigToml);
            Assert.Contains("network_name = \"n\"", p.ConfigToml);
        }
        else
        {
            Assert.Equal(before, p.ConfigToml);
        }
    }

    [Fact]
    public void ClientConfigStore_MigratesOldDefaultListenersOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"et_cfg_listen_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var path = Path.Combine(root, "config", "client.json");
            File.WriteAllText(path, """
                { "networks": [
                  { "network_id": "a", "config_toml": "instance_name = \"a\"\nlisteners = [\"tcp://0.0.0.0:11010\", \"udp://0.0.0.0:11010\"]\n" },
                  { "network_id": "b", "config_toml": "instance_name = \"b\"\nlisteners = [\"tcp://0.0.0.0:12345\"]\n" },
                  { "network_id": "c", "network_name": "legacy", "listeners": ["tcp://0.0.0.0:11010", "udp://0.0.0.0:11010"] }
                ] }
                """);

            var store = new ClientConfigStore(root);
            var cfg = store.Load();
            Assert.Equal(ClientConfig.CurrentConfigVersion, cfg.ConfigVersion);
            Assert.Contains("listeners = []", cfg.Networks[0].ConfigToml);
            Assert.Contains("tcp://0.0.0.0:12345", cfg.Networks[1].ConfigToml);
            Assert.Contains("listeners = []", cfg.Networks[2].ConfigToml);

            // 迁移只做一次：之后用户手动改回 11010 不会被再次清掉
            cfg.Networks[0].ConfigToml = cfg.Networks[0].ConfigToml.Replace(
                "listeners = []", "listeners = [\"tcp://0.0.0.0:11010\", \"udp://0.0.0.0:11010\"]");
            store.Save(cfg);
            Assert.Contains("11010", new ClientConfigStore(root).Load().Networks[0].ConfigToml);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PeersWithoutVirtualIp_AreHidden_ExceptSelf()
    {
        Assert.False(EasyTierManager.IsVisiblePeer(new HostPeerInfo { Hostname = "easytier-cf-relay" }));
        Assert.True(EasyTierManager.IsVisiblePeer(new HostPeerInfo { Ipv4 = "10.10.10.10" }));
        Assert.True(EasyTierManager.IsVisiblePeer(new HostPeerInfo { IsSelf = true }));
    }
}
