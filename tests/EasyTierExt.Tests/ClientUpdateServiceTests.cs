using System.IO.Compression;
using EasyTierExt.HostCore.Plugins;
using EasyTierExt.HostCore.Update;
using Xunit;

namespace EasyTierExt.Tests;

public class ClientUpdateServiceTests
{
    [Fact]
    public void ParseReleaseJson_SelectsClientZipAsset_IgnoresPluginZip_AndParsesSha256()
    {
        const string json = """
        {
          "tag_name": "v1.2.0",
          "name": "v1.2.0 Release",
          "body": "1. 新增客户端在线更新\n2. 修复已知问题",
          "html_url": "https://github.com/0511150/Easytier_Extension/releases/tag/v1.2.0",
          "assets": [
            {
              "name": "screen-share-1.0.0.zip",
              "browser_download_url": "https://github.com/0511150/Easytier_Extension/releases/download/v1.2.0/screen-share-1.0.0.zip",
              "size": 12345,
              "digest": "sha256:aaaa"
            },
            {
              "name": "EasyTierWithExtension-win-x64-v1.2.0.zip",
              "browser_download_url": "https://github.com/0511150/Easytier_Extension/releases/download/v1.2.0/EasyTierWithExtension-win-x64-v1.2.0.zip",
              "size": 98765432,
              "digest": "sha256:ABCDEF0123456789"
            }
          ]
        }
        """;

        var result = ClientUpdateService.ParseReleaseJson(json, "1.0.0");
        Assert.True(result.HasUpdate);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.Equal("v1.2.0", result.TagName);
        Assert.Equal("EasyTierWithExtension-win-x64-v1.2.0.zip", result.AssetName);
        Assert.Equal("ABCDEF0123456789", result.ExpectedSha256);
        Assert.Contains("新增客户端在线更新", result.ReleaseNotes);

        var same = ClientUpdateService.ParseReleaseJson(json, "1.2.0");
        Assert.False(same.HasUpdate);
    }

    [Fact]
    public void StageFromZipFile_ValidatesSha256_AndResolvesEasyTierClientExe()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"et_upd_test_{Guid.NewGuid():N}");
        var srcDir = Path.Combine(tempRoot, "src");
        var stagingDir = Path.Combine(tempRoot, "stage");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(stagingDir);

        try
        {
            File.WriteAllBytes(Path.Combine(srcDir, "EasyTierClient.exe"), [0x4D, 0x5A, 0x90, 0x00]);
            var zipPath = Path.Combine(tempRoot, "client.zip");
            ZipFile.CreateFromDirectory(srcDir, zipPath);
            var sha = MarketplaceService.ComputeSha256(zipPath);

            Assert.Throws<InvalidDataException>(() =>
                ClientUpdateService.StageFromZipFile(zipPath, stagingDir, "1.2.0", "0000bad"));

            var staged = ClientUpdateService.StageFromZipFile(zipPath, stagingDir, "1.2.0", sha);
            Assert.True(File.Exists(Path.Combine(staged.ExtractedAppRoot, "EasyTierClient.exe")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void BuildUpdaterPowerShellScript_EscapesQuotesAndIncludesRobocopyAndRestart()
    {
        var script = ClientUpdateService.BuildUpdaterPowerShellScript(
            @"C:\Temp\stage'1",
            @"C:\Temp\stage'1\extracted",
            @"C:\Users\admin\OneDrive\桌面\Easytier_Extension\dist\",
            4321);

        Assert.Contains("Wait-Process -Id 4321", script);
        Assert.Contains("stage''1", script);
        Assert.Contains("robocopy", script);
        Assert.Contains("EasyTierClient.exe", script);
    }
}
