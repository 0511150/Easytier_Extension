using System.Xml.Linq;
using EasyTierExt.HostCore.Configuration;
using Xunit;

namespace EasyTierExt.Tests;

public class AutoStartServiceTests
{
    [Fact]
    public void BuildTaskXml_ProducesValidXml_WithHighestAvailableAndAutoStartArg()
    {
        var exePath = @"C:\Program Files\EasyTier & Extension\中文目录\EasyTierClient.exe";
        var userId = @"DESKTOP-TEST\admin";

        var xml = AutoStartService.BuildTaskXml(exePath, userId);
        var doc = XDocument.Parse(xml);

        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("PT0S", doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal(exePath, doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal(AutoStartService.AutoStartArg, doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"C:\Program Files\EasyTier & Extension\中文目录", doc.Descendants(ns + "WorkingDirectory").Single().Value);
        Assert.Equal(userId, doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
    }

    [Fact]
    public void ClientConfig_RunOnStartup_RoundTripsViaStore()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"et_autostart_cfg_{Guid.NewGuid():N}");
        try
        {
            var store = new ClientConfigStore(tempRoot);
            var cfg = store.Load();
            Assert.False(cfg.RunOnStartup);

            cfg.RunOnStartup = true;
            store.Save(cfg);

            var reloaded = store.Load();
            Assert.True(reloaded.RunOnStartup);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
