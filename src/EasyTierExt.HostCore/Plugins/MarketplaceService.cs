using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Configuration;

namespace EasyTierExt.HostCore.Plugins;

public sealed class InstalledPluginInfo
{
    public required PluginManifest Manifest { get; init; }
    public required string DirectoryPath { get; init; }
    public bool Enabled { get; set; }
    public bool Running { get; set; }
}

public sealed class SourcedRegistryPlugin
{
    public required RegistryPluginEntry Entry { get; init; }
    public required string SourceId { get; init; }
    public required string SourceName { get; init; }
    public required string SourceUrl { get; init; }
    public bool IsOfficial { get; init; }
}

public sealed class MarketplaceService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _baseDirectory;
    private readonly string _pluginsRoot;

    public MarketplaceService(string appBaseDirectory, string? userDataDirectory = null)
    {
        _baseDirectory = appBaseDirectory;
        _pluginsRoot = Path.Combine(userDataDirectory ?? appBaseDirectory, "plugins");
        Directory.CreateDirectory(_pluginsRoot);
    }

    public string PluginsRoot => _pluginsRoot;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyTierWithExtension/1.0");
        return client;
    }

    public static string ApplyGithubMirror(string url, string? mirrorPrefix)
    {
        if (string.IsNullOrWhiteSpace(mirrorPrefix) || string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return url;
        }

        var host = uri.Host;
        bool isGithub =
            host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

        if (!isGithub)
        {
            return url;
        }

        var prefix = mirrorPrefix.Trim().TrimEnd('/');
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return $"{prefix}/{url}";
    }

    public static bool IsUpdateAvailable(string? installedVersion, string? remoteVersion)
    {
        if (!TryParseComparableVersion(installedVersion, out var installed) ||
            !TryParseComparableVersion(remoteVersion, out var remote))
        {
            return false;
        }
        return remote > installed;
    }

    private static bool TryParseComparableVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var cleaned = raw.Trim().TrimStart('v', 'V');
        var dashIdx = cleaned.IndexOfAny(['-', '+']);
        if (dashIdx >= 0)
        {
            cleaned = cleaned[..dashIdx];
        }

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], out var majorOnly))
        {
            version = new Version(majorOnly, 0, 0);
            return true;
        }
        if (parts.Length == 2 && int.TryParse(parts[0], out var maj) && int.TryParse(parts[1], out var min))
        {
            version = new Version(maj, min, 0);
            return true;
        }

        return Version.TryParse(cleaned, out version!);
    }

    public List<InstalledPluginInfo> ScanInstalledPlugins(IReadOnlyDictionary<string, bool> savedStates)
    {
        var byId = new Dictionary<string, InstalledPluginInfo>(StringComparer.OrdinalIgnoreCase);
        ScanDirectory(_pluginsRoot, savedStates, byId);
        return [.. byId.Values];
    }

    private static void ScanDirectory(
        string root,
        IReadOnlyDictionary<string, bool> savedStates,
        Dictionary<string, InstalledPluginInfo> target)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null)
                {
                    continue;
                }

                var (ok, _) = MenuNamingValidator.Validate(manifest);
                if (!ok)
                {
                    continue;
                }

                var enabled = savedStates.TryGetValue(manifest.Id, out var state) && state;
                target[manifest.Id] = new InstalledPluginInfo
                {
                    Manifest = manifest,
                    DirectoryPath = dir,
                    Enabled = enabled
                };
            }
            catch
            {
            }
        }
    }

    public async Task<RegistryIndex> FetchRegistryAsync(string source, CancellationToken ct = default)
    {
        string json;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var indexCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            indexCts.CancelAfter(TimeSpan.FromSeconds(15));
            json = await Http.GetStringAsync(uri, indexCts.Token).ConfigureAwait(false);
        }
        else
        {
            var localPath = Path.IsPathRooted(source) ? source : Path.Combine(_baseDirectory, source);
            if (!File.Exists(localPath))
            {
                return new RegistryIndex();
            }
            json = await File.ReadAllTextAsync(localPath, ct).ConfigureAwait(false);
        }

        return JsonSerializer.Deserialize<RegistryIndex>(json, JsonOptions) ?? new RegistryIndex();
    }

    public async Task<List<SourcedRegistryPlugin>> FetchOfficialStoreAsync(
        MarketplaceSourceConfig officialSource,
        string? mirrorPrefix = null,
        CancellationToken ct = default)
    {
        RegistryIndex? index = null;
        string effectiveSourceUrl = officialSource.Url;
        officialSource.LastLoadFailed = false;

        bool isPlaceholder = string.IsNullOrWhiteSpace(officialSource.Url) ||
                             officialSource.Url.Contains("github-owner", StringComparison.OrdinalIgnoreCase);

        if (!isPlaceholder)
        {
            try
            {
                var url = ApplyGithubMirror(officialSource.Url, mirrorPrefix);
                index = await FetchRegistryAsync(url, ct).ConfigureAwait(false);
            }
            catch
            {
                officialSource.LastLoadFailed = true;
                index = null;
            }
        }
        else
        {
            officialSource.LastLoadFailed = true;
        }

        index ??= new RegistryIndex();
        officialSource.CachedPluginCount = index.Plugins.Count;
        return index.Plugins.Select(p => new SourcedRegistryPlugin
        {
            Entry = p,
            SourceId = officialSource.Id,
            SourceName = string.IsNullOrWhiteSpace(officialSource.Name) ? "官方插件商店" : officialSource.Name,
            SourceUrl = effectiveSourceUrl,
            IsOfficial = true
        }).ToList();
    }

    public async Task<List<SourcedRegistryPlugin>> FetchThirdPartyStoresAsync(
        IEnumerable<MarketplaceSourceConfig> sources,
        string? mirrorPrefix = null,
        CancellationToken ct = default)
    {
        var enabledThirdParty = sources.Where(s => !s.IsOfficial && s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabledThirdParty.Count == 0)
        {
            return [];
        }

        var tasks = enabledThirdParty.Select(async src =>
        {
            try
            {
                var url = ApplyGithubMirror(src.Url, mirrorPrefix);
                var idx = await FetchRegistryAsync(url, ct).ConfigureAwait(false);
                src.CachedPluginCount = idx.Plugins.Count;
                return idx.Plugins.Select(p => new SourcedRegistryPlugin
                {
                    Entry = p,
                    SourceId = src.Id,
                    SourceName = string.IsNullOrWhiteSpace(src.Name) ? src.Url : src.Name,
                    SourceUrl = src.Url,
                    IsOfficial = false
                }).ToList();
            }
            catch
            {
                return new List<SourcedRegistryPlugin>();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(x => x).ToList();
    }

    public static Uri ResolveDownloadUri(string downloadUrl, string registrySourceUrl)
    {
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var absUri) &&
            (absUri.Scheme == Uri.UriSchemeHttp || absUri.Scheme == Uri.UriSchemeHttps))
        {
            return absUri;
        }

        if (Uri.TryCreate(registrySourceUrl, UriKind.Absolute, out var regUri) &&
            (regUri.Scheme == Uri.UriSchemeHttp || regUri.Scheme == Uri.UriSchemeHttps))
        {
            return new Uri(regUri, downloadUrl);
        }

        throw new InvalidOperationException($"无法解析插件下载地址: {downloadUrl}");
    }

    public Task<PluginManifest> InstallFromRegistryAsync(
        RegistryPluginEntry entry,
        string registrySource,
        CancellationToken ct = default)
        => InstallFromRegistryAsync(entry, registrySource, null, null, ct);

    public async Task<PluginManifest> InstallFromRegistryAsync(
        RegistryPluginEntry entry,
        string registrySource,
        string? githubMirrorPrefix,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        EnsureHostVersion(entry.Name, entry.MinHostVersion);
        var tempZip = Path.Combine(Path.GetTempPath(), $"et_plugin_{Guid.NewGuid():N}.zip");
        try
        {
            if ((Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var absUri) &&
                 (absUri.Scheme == Uri.UriSchemeHttp || absUri.Scheme == Uri.UriSchemeHttps)) ||
                (Uri.TryCreate(registrySource, UriKind.Absolute, out var regUri) &&
                 (regUri.Scheme == Uri.UriSchemeHttp || regUri.Scheme == Uri.UriSchemeHttps)))
            {
                var resolved = ResolveDownloadUri(entry.DownloadUrl, registrySource);
                await DownloadWithOptionalMirrorAsync(resolved.ToString(), tempZip, githubMirrorPrefix, progress, ct).ConfigureAwait(false);
            }
            else
            {
                var regPath = Path.IsPathRooted(registrySource) ? registrySource : Path.Combine(_baseDirectory, registrySource);
                var regDir = Path.GetDirectoryName(regPath) ?? _baseDirectory;
                var zipSource = Path.IsPathRooted(entry.DownloadUrl)
                    ? entry.DownloadUrl
                    : Path.Combine(regDir, entry.DownloadUrl);
                if (!File.Exists(zipSource))
                {
                    throw new FileNotFoundException($"找不到插件包: {zipSource}");
                }
                File.Copy(zipSource, tempZip, overwrite: true);
                progress?.Report(100);
            }

            if (!string.IsNullOrWhiteSpace(entry.Sha256))
            {
                var actualHash = ComputeSha256(tempZip);
                if (!string.Equals(actualHash, entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA256 校验失败: 期望 {entry.Sha256}, 实际 {actualHash}");
                }
            }

            return InstallFromLocalZip(tempZip);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    public PluginManifest InstallFromLocalZip(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("找不到指定的插件压缩包。", zipPath);
        }

        var stagingDir = Path.Combine(Path.GetTempPath(), $"et_stage_{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, stagingDir);

            // Support zip with root folder or flat files
            var rootDir = stagingDir;
            if (!File.Exists(Path.Combine(rootDir, "manifest.json")))
            {
                var subDirs = Directory.GetDirectories(stagingDir);
                if (subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "manifest.json")))
                {
                    rootDir = subDirs[0];
                }
                else
                {
                    throw new InvalidDataException("插件包内缺少 manifest.json。");
                }
            }

            var manifestJson = File.ReadAllText(Path.Combine(rootDir, "manifest.json"));
            var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, JsonOptions)
                ?? throw new InvalidDataException("manifest.json 解析失败。");

            var (ok, error) = MenuNamingValidator.Validate(manifest);
            if (!ok)
            {
                throw new InvalidDataException($"插件清单校验失败: {error}");
            }

            EnsureHostVersion(manifest.Name, manifest.MinHostVersion);

            var entryPath = Path.Combine(rootDir, manifest.Entry);
            if (!File.Exists(entryPath))
            {
                throw new InvalidDataException($"插件包内缺少入口程序: {manifest.Entry}");
            }

            var targetDir = Path.Combine(_pluginsRoot, manifest.Id);
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }
            CopyDirectory(rootDir, targetDir);

            return manifest;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }
        }
    }

    /// <summary>主程序版本是否满足插件声明的 min_host_version；无法解析的版本号视为满足。</summary>
    public static bool IsHostVersionSatisfied(string? minHostVersion, string hostVersion)
    {
        static Version? Parse(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var core = v.Trim().TrimStart('v', 'V').Split('-', '+')[0];
            return Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed) ? parsed : null;
        }

        var min = Parse(minHostVersion);
        var host = Parse(hostVersion);
        return min is null || host is null || host >= min;
    }

    private static void EnsureHostVersion(string pluginName, string? minHostVersion)
    {
        var host = Update.ClientUpdateService.GetCurrentVersion();
        if (!IsHostVersionSatisfied(minHostVersion, host))
        {
            throw new InvalidOperationException($"“{pluginName}”需要主程序 {minHostVersion} 或更高版本（当前 {host}），请先更新主程序。");
        }
    }

    public void UninstallPlugin(string pluginId)
    {
        var targetDir = Path.Combine(_pluginsRoot, pluginId);
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }
    }

    public static string ComputeSha256(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var hash = SHA256.HashData(fs);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task DownloadWithOptionalMirrorAsync(
        string rawUrl,
        string destinationPath,
        string? mirrorPrefix,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var mirroredUrl = ApplyGithubMirror(rawUrl, mirrorPrefix);
        if (!string.Equals(mirroredUrl, rawUrl, StringComparison.Ordinal))
        {
            try
            {
                await DownloadFileAsync(new Uri(mirroredUrl), destinationPath, progress, ct).ConfigureAwait(false);
                return;
            }
            catch when (!ct.IsCancellationRequested)
            {
                // Fall back to direct origin URL if mirror fails
            }
        }

        await DownloadFileAsync(new Uri(rawUrl), destinationPath, progress, ct).ConfigureAwait(false);
    }

    private static async Task DownloadFileAsync(
        Uri uri,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = File.Create(destinationPath);

        var buffer = new byte[64 * 1024];
        long totalRead = 0;
        int lastPct = -1;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalRead += read;
            if (totalBytes > 0 && progress is not null)
            {
                int pct = (int)Math.Clamp(totalRead * 100L / totalBytes, 0, 100);
                if (pct != lastPct)
                {
                    lastPct = pct;
                    progress.Report(pct);
                }
            }
        }

        progress?.Report(100);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
