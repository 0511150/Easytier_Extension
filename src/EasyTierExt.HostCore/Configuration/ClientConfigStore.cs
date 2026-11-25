using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyTierExt.HostCore.Configuration;

public sealed class MarketplaceSourceConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("is_official")]
    public bool IsOfficial { get; set; }

    [JsonPropertyName("cached_plugin_count")]
    public int CachedPluginCount { get; set; }
}

public sealed class ClientConfig
{
    public const string OfficialStoreUrl = "https://0511150.github.io/Easytier_Extension_Registry/registry.json";

    [JsonPropertyName("marketplace_url")]
    public string MarketplaceUrl { get; set; } = "market/registry.json";

    [JsonPropertyName("marketplace_sources")]
    public List<MarketplaceSourceConfig> MarketplaceSources { get; set; } = [];

    [JsonPropertyName("github_mirror_prefix")]
    public string GithubMirrorPrefix { get; set; } = string.Empty;

    [JsonPropertyName("minimize_to_tray")]
    public bool MinimizeToTray { get; set; } = true;

    [JsonPropertyName("run_on_startup")]
    public bool RunOnStartup { get; set; }

    [JsonPropertyName("networks")]
    public List<NetworkProfile> Networks { get; set; } = [];

    public void EnsureSourcesNormalized()
    {
        MarketplaceSources ??= [];
        var official = MarketplaceSources.FirstOrDefault(s => s.IsOfficial);
        if (official is null)
        {
            official = new MarketplaceSourceConfig
            {
                Id = "official",
                Name = "官方插件商店",
                Url = OfficialStoreUrl,
                Enabled = true,
                IsOfficial = true
            };
            MarketplaceSources.Insert(0, official);
        }
        else
        {
            official.Id = string.IsNullOrWhiteSpace(official.Id) ? "official" : official.Id;
            official.Name = "官方插件商店";
            official.Enabled = true;
            if (string.IsNullOrWhiteSpace(official.Url) ||
                official.Url.Contains("github-owner", StringComparison.OrdinalIgnoreCase))
            {
                official.Url = OfficialStoreUrl;
            }
            var idx = MarketplaceSources.IndexOf(official);
            if (idx > 0)
            {
                MarketplaceSources.RemoveAt(idx);
                MarketplaceSources.Insert(0, official);
            }
        }

        if (!string.IsNullOrWhiteSpace(MarketplaceUrl) &&
            !string.Equals(MarketplaceUrl.Trim(), "market/registry.json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(MarketplaceUrl.Trim(), OfficialStoreUrl, StringComparison.OrdinalIgnoreCase))
        {
            var customUrl = MarketplaceUrl.Trim();
            if (!MarketplaceSources.Any(s => string.Equals(s.Url.Trim(), customUrl, StringComparison.OrdinalIgnoreCase)))
            {
                MarketplaceSources.Add(new MarketplaceSourceConfig
                {
                    Name = "自定义插件源",
                    Url = customUrl,
                    Enabled = true,
                    IsOfficial = false
                });
            }
            MarketplaceUrl = "market/registry.json";
        }
    }
}

public sealed class NetworkProfile
{
    [JsonPropertyName("network_id")]
    public string NetworkId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "默认网络";

    [JsonPropertyName("network_name")]
    public string NetworkName { get; set; } = "easytier-ext";

    [JsonPropertyName("network_secret")]
    public string NetworkSecret { get; set; } = "easytier-secret";

    /// <summary>
    /// "public" | "manual" | "standalone"
    /// </summary>
    [JsonPropertyName("connection_mode")]
    public string ConnectionMode { get; set; } = "public";

    [JsonPropertyName("public_server")]
    public string PublicServer { get; set; } = "tcp://public.easytier.top:11010";

    [JsonPropertyName("peer_urls")]
    public List<string> PeerUrls { get; set; } = [];

    /// <summary>
    /// "dhcp" | "static"
    /// </summary>
    [JsonPropertyName("ipv4_mode")]
    public string Ipv4Mode { get; set; } = "dhcp";

    [JsonPropertyName("static_ipv4")]
    public string StaticIpv4 { get; set; } = "10.10.10.10/24";

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = Environment.MachineName;

    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; }

    // Advanced Configuration
    [JsonPropertyName("proxy_cidrs")]
    public List<string> ProxyCidrs { get; set; } = [];

    [JsonPropertyName("listeners")]
    public List<string> Listeners { get; set; } = ["tcp://0.0.0.0:11010", "udp://0.0.0.0:11010"];

    [JsonPropertyName("disable_encryption")]
    public bool DisableEncryption { get; set; }

    [JsonPropertyName("encryption_algorithm")]
    public string EncryptionAlgorithm { get; set; } = "aes-gcm";

    [JsonPropertyName("dev_name")]
    public string DevName { get; set; } = string.Empty;

    [JsonPropertyName("mtu")]
    public int Mtu { get; set; } = 1360;

    [JsonPropertyName("latency_first")]
    public bool LatencyFirst { get; set; }

    [JsonPropertyName("enable_kcp_proxy")]
    public bool EnableKcpProxy { get; set; }

    [JsonPropertyName("enable_quic_proxy")]
    public bool EnableQuicProxy { get; set; }

    [JsonPropertyName("no_tun")]
    public bool NoTun { get; set; }
}

public sealed class ClientConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _configPath;
    private readonly string _pluginsStatePath;
    private readonly string? _legacyAppBaseDirectory;

    public static string GetDefaultAppDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Easytier_With_Extension");

    public ClientConfigStore(string dataRootDirectory, string? legacyAppBaseDirectory = null)
    {
        _legacyAppBaseDirectory = legacyAppBaseDirectory;
        var configDir = Path.Combine(dataRootDirectory, "config");
        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "client.json");
        _pluginsStatePath = Path.Combine(configDir, "plugins_state.json");
    }

    public ClientConfig Load()
    {
        if (!File.Exists(_configPath) && !string.IsNullOrWhiteSpace(_legacyAppBaseDirectory))
        {
            var legacyConfigPath = Path.Combine(_legacyAppBaseDirectory, "config", "client.json");
            if (File.Exists(legacyConfigPath))
            {
                try { File.Copy(legacyConfigPath, _configPath, overwrite: false); } catch { }
            }
        }

        if (!File.Exists(_configPath))
        {
            var defaultConfig = new ClientConfig
            {
                Networks = [new NetworkProfile()]
            };
            defaultConfig.EnsureSourcesNormalized();
            Save(defaultConfig);
            return defaultConfig;
        }

        var json = File.ReadAllText(_configPath);
        var cfg = JsonSerializer.Deserialize<ClientConfig>(json, JsonOptions) ?? new ClientConfig();
        if (cfg.Networks.Count == 0)
        {
            cfg.Networks.Add(new NetworkProfile());
        }
        cfg.EnsureSourcesNormalized();
        return cfg;
    }

    public void Save(ClientConfig config)
    {
        config.EnsureSourcesNormalized();
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    public Dictionary<string, bool> LoadPluginStates()
    {
        if (!File.Exists(_pluginsStatePath) && !string.IsNullOrWhiteSpace(_legacyAppBaseDirectory))
        {
            var legacyStatePath = Path.Combine(_legacyAppBaseDirectory, "config", "plugins_state.json");
            if (File.Exists(legacyStatePath))
            {
                try { File.Copy(legacyStatePath, _pluginsStatePath, overwrite: false); } catch { }
            }
        }

        if (!File.Exists(_pluginsStatePath))
        {
            var defaults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["screen-share"] = true
            };
            SavePluginStates(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_pluginsStatePath);
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(json, JsonOptions)
                ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SavePluginStates(Dictionary<string, bool> states)
    {
        var json = JsonSerializer.Serialize(states, JsonOptions);
        File.WriteAllText(_pluginsStatePath, json);
    }
}
