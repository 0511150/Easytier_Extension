using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyTierExt.HostCore.Plugins;

namespace EasyTierExt.HostCore.Update;

public sealed class ClientUpdateCheckResult
{
    public string CurrentVersion { get; init; } = "1.0.0";
    public string LatestVersion { get; init; } = string.Empty;
    public string TagName { get; init; } = string.Empty;
    public bool HasUpdate { get; init; }
    public string ReleaseName { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = ClientUpdateService.ReleasesPageUrl;
    public string AssetName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public long AssetSizeBytes { get; init; }
    public string? ExpectedSha256 { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed class StagedClientUpdate
{
    public required string StagingRootDirectory { get; init; }
    public required string ExtractedAppRoot { get; init; }
    public required string TargetVersion { get; init; }
}

public sealed class ClientUpdateService
{
    public const string DefaultRepo = "0511150/Easytier_Extension";
    public const string LatestReleaseApiUrl = $"https://api.github.com/repos/{DefaultRepo}/releases/latest";
    public const string ReleasesPageUrl = $"https://github.com/{DefaultRepo}/releases";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static string GetCurrentVersion()
    {
        var entry = Assembly.GetEntryAssembly();
        var asm = entry is not null &&
                  string.Equals(entry.GetName().Name, "EasyTierClient", StringComparison.OrdinalIgnoreCase)
            ? entry
            : typeof(ClientUpdateService).Assembly;
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            var plusIdx = infoVer.IndexOf('+');
            var clean = (plusIdx >= 0 ? infoVer[..plusIdx] : infoVer).Trim().TrimStart('v', 'V');
            if (!string.IsNullOrWhiteSpace(clean))
            {
                return clean;
            }
        }

        var ver = asm.GetName().Version;
        if (ver is not null)
        {
            return $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
        }
        return "1.1.0";
    }

    public async Task<ClientUpdateCheckResult> CheckForUpdateAsync(
        string? currentVersionOverride = null,
        CancellationToken ct = default)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(TimeSpan.FromSeconds(15));
        var json = await Http.GetStringAsync(LatestReleaseApiUrl, reqCts.Token).ConfigureAwait(false);
        return ParseReleaseJson(json, currentVersionOverride ?? GetCurrentVersion());
    }

    public static ClientUpdateCheckResult ParseReleaseJson(string json, string currentVersion)
    {
        var dto = JsonSerializer.Deserialize<GitHubReleaseDto>(json, JsonOptions)
            ?? throw new InvalidDataException("GitHub Release 响应为空或格式无效。");

        var tag = dto.TagName?.Trim() ?? string.Empty;
        var latestVer = tag.TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(latestVer) && !string.IsNullOrWhiteSpace(dto.Name))
        {
            latestVer = dto.Name.Trim().TrimStart('v', 'V');
        }

        var assets = dto.Assets ?? [];
        var clientAsset = assets.FirstOrDefault(a =>
                              !string.IsNullOrWhiteSpace(a.Name) &&
                              a.Name.StartsWith("EasyTierWithExtension", StringComparison.OrdinalIgnoreCase) &&
                              a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                          ?? assets.FirstOrDefault(a =>
                              !string.IsNullOrWhiteSpace(a.Name) &&
                              a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                              !a.Name.StartsWith("screen-share", StringComparison.OrdinalIgnoreCase) &&
                              (a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) ||
                               a.Name.Contains("client", StringComparison.OrdinalIgnoreCase)));

        string? expectedSha256 = null;
        if (!string.IsNullOrWhiteSpace(clientAsset?.Digest) &&
            clientAsset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            expectedSha256 = clientAsset.Digest["sha256:".Length..].Trim();
        }

        return new ClientUpdateCheckResult
        {
            CurrentVersion = currentVersion,
            LatestVersion = latestVer,
            TagName = string.IsNullOrWhiteSpace(tag) ? $"v{latestVer}" : tag,
            HasUpdate = MarketplaceService.IsUpdateAvailable(currentVersion, tag),
            ReleaseName = string.IsNullOrWhiteSpace(dto.Name) ? tag : dto.Name.Trim(),
            ReleaseNotes = dto.Body?.Trim() ?? string.Empty,
            HtmlUrl = string.IsNullOrWhiteSpace(dto.HtmlUrl) ? ReleasesPageUrl : dto.HtmlUrl.Trim(),
            AssetName = clientAsset?.Name ?? string.Empty,
            DownloadUrl = clientAsset?.BrowserDownloadUrl ?? string.Empty,
            AssetSizeBytes = clientAsset?.Size ?? 0,
            ExpectedSha256 = expectedSha256,
            PublishedAt = dto.PublishedAt
        };
    }

    public async Task<StagedClientUpdate> DownloadAndStageUpdateAsync(
        ClientUpdateCheckResult check,
        string? githubMirrorPrefix,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(check.DownloadUrl))
        {
            throw new InvalidOperationException("该 Release 暂未附带客户端更新包（EasyTierWithExtension-win-x64-*.zip），请前往 GitHub Releases 页面手动查看。");
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"et_client_update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        var zipPath = Path.Combine(stagingRoot, "package.zip");

        try
        {
            await DownloadWithOptionalMirrorAsync(check.DownloadUrl, zipPath, githubMirrorPrefix, progress, ct).ConfigureAwait(false);
            return StageFromZipFile(zipPath, stagingRoot, check.LatestVersion, check.ExpectedSha256);
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
            }
            throw;
        }
    }

    public static StagedClientUpdate StageFromZipFile(
        string zipPath,
        string stagingRoot,
        string targetVersion,
        string? expectedSha256 = null)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("找不到下载的更新压缩包。", zipPath);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = MarketplaceService.ComputeSha256(zipPath);
            if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"更新包 SHA256 校验失败: 期望 {expectedSha256}，实际 {actual}");
            }
        }

        var extractDir = Path.Combine(stagingRoot, "extracted");
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var appRoot = extractDir;
        if (!File.Exists(Path.Combine(appRoot, "EasyTierClient.exe")))
        {
            var subDirs = Directory.GetDirectories(extractDir);
            if (subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "EasyTierClient.exe")))
            {
                appRoot = subDirs[0];
            }
            else
            {
                throw new InvalidDataException("更新压缩包内缺少 EasyTierClient.exe。");
            }
        }

        return new StagedClientUpdate
        {
            StagingRootDirectory = stagingRoot,
            ExtractedAppRoot = appRoot,
            TargetVersion = targetVersion
        };
    }

    public static string BuildUpdaterPowerShellScript(
        string stagingRoot,
        string extractedAppRoot,
        string targetAppDir,
        int waitPid)
    {
        static string Esc(string p) => p.TrimEnd('\\').Replace("'", "''");
        var src = Esc(extractedAppRoot);
        var dst = Esc(targetAppDir);
        var stage = Esc(stagingRoot);

        return $$"""
$ErrorActionPreference = 'SilentlyContinue'
Wait-Process -Id {{waitPid}} -Timeout 30 -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

$src = '{{src}}'
$dst = '{{dst}}'
$stage = '{{stage}}'

& robocopy $src $dst /E /IS /IT /R:10 /W:1 /NFL /NDL /NJH /NJS | Out-Null

$exe = Join-Path $dst 'EasyTierClient.exe'
if (Test-Path -LiteralPath $exe) {
    Start-Process -FilePath $exe -WorkingDirectory $dst
}

Start-Sleep -Milliseconds 500
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
""";
    }

    public void LaunchUpdaterScript(StagedClientUpdate staged, string targetAppDir, int currentPid)
    {
        var scriptPath = Path.Combine(staged.StagingRootDirectory, "updater.ps1");
        var scriptContent = BuildUpdaterPowerShellScript(
            staged.StagingRootDirectory,
            staged.ExtractedAppRoot,
            targetAppDir,
            currentPid);

        // Windows PowerShell 5.1 requires UTF-8 BOM to decode non-ASCII paths (e.g. 桌面) properly.
        File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(psExe))
        {
            psExe = "powershell.exe";
        }

        var psi = new ProcessStartInfo
        {
            FileName = psExe,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);

        Process.Start(psi);
    }

    private static async Task DownloadWithOptionalMirrorAsync(
        string rawUrl,
        string destinationPath,
        string? mirrorPrefix,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var mirrored = MarketplaceService.ApplyGithubMirror(rawUrl, mirrorPrefix);
        if (!string.Equals(mirrored, rawUrl, StringComparison.Ordinal))
        {
            try
            {
                await DownloadFileAsync(new Uri(mirrored), destinationPath, progress, ct).ConfigureAwait(false);
                return;
            }
            catch when (!ct.IsCancellationRequested)
            {
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

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
