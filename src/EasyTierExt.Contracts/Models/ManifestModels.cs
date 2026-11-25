using System.Text.Json.Serialization;

namespace EasyTierExt.Contracts.Models;

public sealed class PluginManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "🧩";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "扩展工具";

    [JsonPropertyName("homepage")]
    public string Homepage { get; set; } = string.Empty;

    [JsonPropertyName("min_host_version")]
    public string MinHostVersion { get; set; } = "1.0.0";

    [JsonPropertyName("entry")]
    public string Entry { get; set; } = string.Empty;

    [JsonPropertyName("menus")]
    public List<PluginMenuSpec> Menus { get; set; } = [];
}

public sealed class PluginMenuSpec
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

public static class MenuNamingValidator
{
    public static (bool IsValid, string? ErrorMessage) Validate(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            return (false, "manifest.id 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            return (false, "manifest.name 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry))
        {
            return (false, "manifest.entry 不能为空。");
        }

        if (manifest.Menus.Count == 0)
        {
            return (false, "manifest.menus 至少需要声明一个菜单项。");
        }

        var expectedPrefix = $"{manifest.Name} - ";
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var menu in manifest.Menus)
        {
            if (string.IsNullOrWhiteSpace(menu.Id))
            {
                return (false, "菜单项 id 不能为空。");
            }

            if (!seenIds.Add(menu.Id))
            {
                return (false, $"菜单项 id '{menu.Id}' 重复。");
            }

            if (string.IsNullOrWhiteSpace(menu.Title) ||
                !menu.Title.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(menu.Title[expectedPrefix.Length..]))
            {
                return (false, $"菜单项 '{menu.Title}' 不符合强制命名规范，必须以 '{expectedPrefix}<菜单名>' 格式命名。");
            }
        }

        return (true, null);
    }
}

public sealed class RegistryIndex
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("store_name")]
    public string StoreName { get; set; } = string.Empty;

    [JsonPropertyName("store_description")]
    public string StoreDescription { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("plugins")]
    public List<RegistryPluginEntry> Plugins { get; set; } = [];
}

public sealed class RegistryPluginEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "🧩";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "扩展工具";

    [JsonPropertyName("homepage")]
    public string Homepage { get; set; } = string.Empty;

    [JsonPropertyName("min_host_version")]
    public string MinHostVersion { get; set; } = "1.0.0";

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("published_at")]
    public string PublishedAt { get; set; } = string.Empty;
}
