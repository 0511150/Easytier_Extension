using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EasyTierExt.HostCore.Configuration;

/// <summary>普通设置表单覆盖的配置项。</summary>
public sealed record NetworkFormValues
{
    public const string ProtocolDefault = "default";
    public const string ProtocolKcp = "kcp";
    public const string ProtocolQuic = "quic";

    public IReadOnlyList<string> Peers { get; init; } = [];
    public string NetworkName { get; init; } = string.Empty;
    public string NetworkSecret { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public bool Dhcp { get; init; }
    public string StaticIpv4 { get; init; } = string.Empty;
    public string Protocol { get; init; } = ProtocolDefault;

    /// <summary>联机兼容：为子网代理和 KCP 代理开启用户态网络栈，并绑定物理网卡。</summary>
    public bool GameCompat { get; init; } = true;

    public bool SamePeers(NetworkFormValues other) => Peers.ToHashSet().SetEquals(other.Peers);

    public bool SameAddress(NetworkFormValues other) =>
        Dhcp == other.Dhcp && (Dhcp || StaticIpv4 == other.StaticIpv4);

    public bool SameAs(NetworkFormValues other) =>
        SamePeers(other) && SameAddress(other) &&
        NetworkName == other.NetworkName && NetworkSecret == other.NetworkSecret &&
        Hostname == other.Hostname && Protocol == other.Protocol && GameCompat == other.GameCompat;
}

/// <summary>
/// 每个网络以一份 EasyTier TOML 配置为唯一数据源；普通设置只是其中若干键的表单视图。
/// </summary>
public static class NetworkConfigToml
{
    public const string TunDeviceName = "EasyTier_With_Extension";
    /// <summary>未写明时默认开启的核心选项：绑定物理网卡、用户态网络栈、转发本机 UDP 广播（局域网游戏发现房间）。</summary>
    private static readonly string[] DefaultOnFlags = ["bind_device", "use_smoltcp", "enable_udp_broadcast_relay"];

    public const string AssistantUrl = "https://easytier.cn/assistant/";

    /// <summary>新建网络时静态地址的默认值。</summary>
    public const string DefaultStaticIpv4 = "10.10.10.255/32";

    /// <summary>静态地址未写掩码时按 /32 处理（与核心一致）。</summary>
    public static string NormalizeIpv4(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length == 0 || v.Contains('/') ? v : v + "/32";
    }

    /// <summary>界面显示用：/32 不显示，其他掩码照常显示。</summary>
    public static string DisplayIpv4(string? value)
    {
        var v = NormalizeIpv4(value);
        return v.EndsWith("/32", StringComparison.Ordinal) ? v[..^3] : v;
    }

    /// <exception cref="FormatException">TOML 语法错误，消息含行号。</exception>
    public static NetworkFormValues Read(string toml) => Read(new TomlDocumentEditor(toml));

    private static NetworkFormValues Read(TomlDocumentEditor doc) => new()
    {
        Peers = doc.GetArrayTableStrings("peer", "uri"),
        NetworkName = doc.GetString("network_identity", "network_name") ?? string.Empty,
        NetworkSecret = doc.GetString("network_identity", "network_secret") ?? string.Empty,
        Hostname = doc.GetString("", "hostname") ?? string.Empty,
        Dhcp = doc.GetBool("", "dhcp"),
        StaticIpv4 = NormalizeIpv4(doc.GetString("", "ipv4")),
        Protocol = doc.GetBool("flags", "enable_kcp_proxy") ? NetworkFormValues.ProtocolKcp
            : doc.GetBool("flags", "enable_quic_proxy") ? NetworkFormValues.ProtocolQuic
            : NetworkFormValues.ProtocolDefault,
        // 未写明时按默认开启处理，保存时会由 EnsureDefaults 补全
        GameCompat = doc.GetBool("flags", "use_smoltcp", true) && doc.GetBool("flags", "bind_device", true)
    };

    /// <summary>仅改写与表单取值不同的键，其余内容原样保留。</summary>
    public static string Apply(string toml, NetworkFormValues form)
    {
        var doc = new TomlDocumentEditor(toml);
        var current = Read(doc);

        if (form.NetworkName != current.NetworkName)
            doc.Set("network_identity", "network_name", TomlDocumentEditor.Quote(form.NetworkName));
        if (form.NetworkSecret != current.NetworkSecret)
            doc.Set("network_identity", "network_secret", TomlDocumentEditor.Quote(form.NetworkSecret));

        if (form.Hostname != current.Hostname)
        {
            if (form.Hostname.Length == 0) doc.Remove("", "hostname");
            else doc.Set("", "hostname", TomlDocumentEditor.Quote(form.Hostname));
        }

        if (!form.SameAddress(current))
        {
            doc.Set("", "dhcp", form.Dhcp ? "true" : "false");
            if (form.Dhcp || form.StaticIpv4.Length == 0) doc.Remove("", "ipv4");
            else doc.Set("", "ipv4", TomlDocumentEditor.Quote(form.StaticIpv4));
        }

        if (!form.SamePeers(current))
            doc.SyncArrayTableStrings("peer", "uri", form.Peers.Distinct().ToList());

        if (form.Protocol != current.Protocol)
        {
            SetFlag(doc, "enable_kcp_proxy", form.Protocol == NetworkFormValues.ProtocolKcp);
            SetFlag(doc, "enable_quic_proxy", form.Protocol == NetworkFormValues.ProtocolQuic);
        }
        if (form.GameCompat != current.GameCompat)
        {
            doc.Set("flags", "use_smoltcp", form.GameCompat ? "true" : "false");
            // 关闭时不动 bind_device：核心本身默认绑定物理网卡，只有开启时才需要保证它为 true
            if (form.GameCompat) doc.Set("flags", "bind_device", "true");
        }
        return doc.Text;
    }

    private static void SetFlag(TomlDocumentEditor doc, string key, bool on)
    {
        if (on) doc.Set("flags", key, "true");
        else doc.Remove("flags", key);
    }

    /// <summary>补全未写明的客户端默认项：网卡名称与 DefaultOnFlags；已写明的取值不变。</summary>
    public static string EnsureDefaults(string toml)
    {
        var doc = new TomlDocumentEditor(toml);
        if (string.IsNullOrWhiteSpace(doc.GetString("flags", "dev_name")))
        {
            doc.Set("flags", "dev_name", TomlDocumentEditor.Quote(TunDeviceName));
        }
        foreach (var key in DefaultOnFlags)
        {
            if (!doc.Contains("flags", key)) doc.Set("flags", key, "true");
        }
        return doc.Text;
    }

    public static string GetDeviceName(string toml) =>
        new TomlDocumentEditor(toml).GetString("flags", "dev_name") is { } name && !string.IsNullOrWhiteSpace(name)
            ? name
            : TunDeviceName;

    /// <summary>同名网卡无法被多个核心实例同时使用，冲突时追加序号。</summary>
    public static string MakeUniqueDeviceName(string name, IReadOnlyCollection<string> inUse)
    {
        var candidate = name;
        for (int i = 2; inUse.Contains(candidate, StringComparer.OrdinalIgnoreCase); i++)
        {
            candidate = $"{name}_{i}";
        }
        return candidate;
    }

    /// <summary>补全配置、校验并刷新 profile 上的缓存字段。</summary>
    /// <exception cref="FormatException">TOML 语法错误或静态地址无效。</exception>
    public static void Store(NetworkProfile profile, string toml)
    {
        var final = EnsureDefaults(toml);
        var values = Read(final);
        ValidateIpv4(values);
        profile.ConfigToml = final;
        RefreshProfile(profile, values);
    }

    /// <summary>旧版配置或新建网络没有 TOML 时由结构化字段生成一份，已有 TOML 则补全默认项。返回配置是否变化。</summary>
    public static bool EnsureGenerated(NetworkProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ConfigToml))
        {
            try
            {
                var completed = EnsureDefaults(profile.ConfigToml);
                bool changed = completed != TomlDocumentEditor.Normalize(profile.ConfigToml);
                profile.ConfigToml = completed;
                RefreshProfile(profile, Read(completed));
                return changed;
            }
            catch (FormatException)
            {
                return false;
            }
        }
        profile.ConfigToml = Generate(profile);
        profile.LegacyFields = null;
        RefreshProfile(profile, Read(profile.ConfigToml));
        return true;
    }

    private static void ValidateIpv4(NetworkFormValues values)
    {
        if (values.Dhcp || values.StaticIpv4.Length == 0) return;
        var parts = values.StaticIpv4.Split('/');
        if (parts.Length != 2 || parts[0].Split('.').Length != 4 ||
            !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out int prefix) || prefix is < 0 or > 32)
        {
            throw new FormatException($"静态 IPv4 地址无效：{values.StaticIpv4}");
        }
    }

    private static void RefreshProfile(NetworkProfile profile, NetworkFormValues values)
    {
        profile.NetworkName = values.NetworkName;
        profile.NetworkSecret = values.NetworkSecret;
        profile.Hostname = values.Hostname.Length == 0 ? Environment.MachineName : values.Hostname;
        profile.PeerUrls = [.. values.Peers];
        profile.Ipv4Mode = values.Dhcp ? "dhcp" : "static";
        if (values.StaticIpv4.Length > 0) profile.StaticIpv4 = values.StaticIpv4;
    }

    private static string Generate(NetworkProfile p)
    {
        var legacy = p.LegacyFields;
        bool noTun = LegacyBool(legacy, "no_tun");
        List<string> peers = LegacyString(legacy, "connection_mode") switch
        {
            "manual" => p.PeerUrls,
            "standalone" => [],
            "public" => [LegacyString(legacy, "public_server") ?? NetworkProfile.DefaultPeer],
            _ => p.PeerUrls
        };
        var listeners = LegacyList(legacy, "listeners") ?? ["tcp://0.0.0.0:11010", "udp://0.0.0.0:11010"];

        var sb = new StringBuilder();
        sb.Append($"instance_name = {Q(p.NetworkId)}\n");
        if (!string.IsNullOrWhiteSpace(p.Hostname)) sb.Append($"hostname = {Q(p.Hostname.Trim())}\n");
        // 旧版的无 TUN 模式从不申请虚拟 IP
        if (!noTun)
        {
            bool isStatic = p.Ipv4Mode == "static" && !string.IsNullOrWhiteSpace(p.StaticIpv4);
            sb.Append(isStatic ? $"ipv4 = {Q(NormalizeIpv4(p.StaticIpv4))}\ndhcp = false\n" : "dhcp = true\n");
        }
        sb.Append($"listeners = [{string.Join(", ", listeners.Select(Q))}]\n");

        sb.Append($"\n[network_identity]\nnetwork_name = {Q(p.NetworkName)}\nnetwork_secret = {Q(p.NetworkSecret)}\n");

        foreach (var peer in peers.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct())
            sb.Append($"\n[[peer]]\nuri = {Q(peer)}\n");

        foreach (var cidr in LegacyList(legacy, "proxy_cidrs") ?? [])
        {
            // 旧版沿用 -n 参数，支持 "真实网段->映射网段"
            var parts = cidr.Split("->", StringSplitOptions.TrimEntries);
            sb.Append($"\n[[proxy_network]]\ncidr = {Q(parts[0])}\n");
            if (parts.Length == 2) sb.Append($"mapped_cidr = {Q(parts[1])}\n");
        }

        sb.Append($"\n[flags]\ndev_name = {Q(TunDeviceName)}\n");
        foreach (var flag in DefaultOnFlags) sb.Append($"{flag} = true\n");
        if (LegacyBool(legacy, "disable_encryption"))
            sb.Append("enable_encryption = false\n");
        else if (LegacyString(legacy, "encryption_algorithm") is { Length: > 0 } alg && alg != "aes-gcm")
            sb.Append($"encryption_algorithm = {Q(alg)}\n");
        if (legacy is not null && legacy.TryGetValue("mtu", out var m) && m.ValueKind == JsonValueKind.Number &&
            m.TryGetInt32(out int mtu) && mtu > 0 && mtu != 1360)
            sb.Append($"mtu = {mtu}\n");
        foreach (var flag in new[] { "latency_first", "enable_kcp_proxy", "enable_quic_proxy", "no_tun" })
        {
            if (LegacyBool(legacy, flag)) sb.Append($"{flag} = true\n");
        }
        return sb.ToString();
    }

    private static string Q(string value) => TomlDocumentEditor.Quote(value);

    private static bool LegacyBool(Dictionary<string, JsonElement>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? LegacyString(Dictionary<string, JsonElement>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string>? LegacyList(Dictionary<string, JsonElement>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
            : null;
}
