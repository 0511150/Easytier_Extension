using System.IO.Compression;
using System.Text.Json;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Configuration;
using EasyTierExt.HostCore.Plugins;
using Xunit;

namespace EasyTierExt.Tests;

public class MarketplaceServiceTests
{
    [Fact]
    public async Task Install_RejectsBadSha256_SucceedsOnMatch_And_Uninstalls()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"et_mkt_{Guid.NewGuid():N}");
        var srcDir = Path.Combine(tempRoot, "src");
        var mktDir = Path.Combine(tempRoot, "market");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(mktDir);

        try
        {
            var manifest = new PluginManifest
            {
                Id = "sample-plug",
                Name = "示例插件",
                Version = "1.0.0",
                Entry = "Sample.exe",
                Menus = [new PluginMenuSpec { Id = "home", Title = "示例插件 - 主页" }]
            };
            File.WriteAllText(Path.Combine(srcDir, "manifest.json"), JsonSerializer.Serialize(manifest));
            File.WriteAllBytes(Path.Combine(srcDir, "Sample.exe"), [0x4D, 0x5A]);

            var zipPath = Path.Combine(mktDir, "sample-1.0.0.zip");
            ZipFile.CreateFromDirectory(srcDir, zipPath);
            var sha = MarketplaceService.ComputeSha256(zipPath);

            var regPath = Path.Combine(mktDir, "registry.json");
            var svc = new MarketplaceService(tempRoot);

            var badEntry = new RegistryPluginEntry { Id = "sample-plug", DownloadUrl = "sample-1.0.0.zip", Sha256 = "0000" };
            await Assert.ThrowsAsync<InvalidDataException>(() => svc.InstallFromRegistryAsync(badEntry, regPath));

            var goodEntry = new RegistryPluginEntry { Id = "sample-plug", DownloadUrl = "sample-1.0.0.zip", Sha256 = sha };
            var installed = await svc.InstallFromRegistryAsync(goodEntry, regPath);
            Assert.Equal("sample-plug", installed.Id);
            Assert.True(File.Exists(Path.Combine(svc.PluginsRoot, "sample-plug", "Sample.exe")));

            svc.UninstallPlugin("sample-plug");
            Assert.False(Directory.Exists(Path.Combine(svc.PluginsRoot, "sample-plug")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ApplyGithubMirror_OnlyRewritesGithubDomains()
    {
        const string mirror = "https://ghfast.top/";
        const string ghUrl = "https://github.com/org/repo/releases/download/v1.0.0/plug.zip";
        const string vendorUrl = "https://cdn.vendor.com/plugins/plug.zip";

        Assert.Equal("https://ghfast.top/https://github.com/org/repo/releases/download/v1.0.0/plug.zip",
            MarketplaceService.ApplyGithubMirror(ghUrl, mirror));
        Assert.Equal(vendorUrl, MarketplaceService.ApplyGithubMirror(vendorUrl, mirror));
        Assert.Equal(ghUrl, MarketplaceService.ApplyGithubMirror(ghUrl, ""));
    }

    [Fact]
    public void IsUpdateAvailable_ComparesSemanticVersions()
    {
        Assert.True(MarketplaceService.IsUpdateAvailable("1.0.0", "1.2.0"));
        Assert.True(MarketplaceService.IsUpdateAvailable("v1.0.0", "v1.0.1"));
        Assert.False(MarketplaceService.IsUpdateAvailable("1.0.0", "1.0.0"));
        Assert.False(MarketplaceService.IsUpdateAvailable("1.2.0", "1.0.0"));
    }

    [Fact]
    public void ClientConfig_NormalizesOfficialSource_AndMigratesLegacyMarketplaceUrl()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"et_src_cfg_{Guid.NewGuid():N}");
        try
        {
            var store = new ClientConfigStore(tempRoot);
            var cfg = new ClientConfig
            {
                MarketplaceUrl = "https://vendor.example.com/registry.json"
            };
            store.Save(cfg);

            var loaded = store.Load();
            Assert.True(loaded.MarketplaceSources.Count >= 2);
            Assert.True(loaded.MarketplaceSources[0].IsOfficial);
            Assert.True(loaded.MarketplaceSources[0].Enabled);
            Assert.Contains(loaded.MarketplaceSources, s => !s.IsOfficial && s.Url == "https://vendor.example.com/registry.json");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
