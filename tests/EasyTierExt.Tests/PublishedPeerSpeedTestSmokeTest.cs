using System.Runtime.CompilerServices;
using System.Text.Json;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Discovery;
using EasyTierExt.HostCore.Native;
using EasyTierExt.HostCore.Plugins;
using Xunit;

namespace EasyTierExt.Tests;

public class PublishedPeerSpeedTestSmokeTest
{
    private const string PluginId = "peer-speedtest";

    [Fact]
    public async Task PublishedPeerSpeedTestExe_Connects_RegistersSchemaAndTables_And_StopsCleanly()
    {
        var root = ResolvePluginRoot();
        if (!File.Exists(Path.Combine(root, "plugins", PluginId, "PeerSpeedTest.exe")))
        {
            return; // 仅在 build-dist.ps1 发布插件后运行
        }

        var tempDataRoot = Path.Combine(Path.GetTempPath(), $"et_smoke_data_{Guid.NewGuid():N}");
        try
        {
            using var job = new JobObjectManager();
            var networks = new List<HostNetworkStatus>();
            await using var discovery = new DiscoveryService(() => networks, _ => [], listenPort: 0);
            var bridge = new SmokeBridge();
            await using var pm = new PluginManager(job, () => networks, _ => [], discovery, bridge,
                $"et_smoke_{Guid.NewGuid():N}", tempDataRoot);
            pm.StartServer();

            var installed = new MarketplaceService(root).ScanInstalledPlugins(new Dictionary<string, bool> { [PluginId] = true });
            Assert.Contains(installed, p => p.Manifest.Id == PluginId);
            pm.SetInstalledPlugins(installed);

            await pm.StartPluginAsync(PluginId);
            var schema = await bridge.SchemaReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var keys = schema.Sections.SelectMany(s => s.Items).Select(i => i.Key).ToList();
            Assert.Contains("peers_table", keys);
            Assert.Contains("history_table", keys);
            Assert.Contains("btn_cancel", keys);

            // 启动后会推送一次节点表与记录表
            var values = await bridge.TablesPushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(values);

            await pm.StopPluginAsync(PluginId);
            Assert.True(Directory.Exists(Path.Combine(tempDataRoot, "plugins_data", PluginId)));
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, true); } catch { }
        }
    }

    private sealed class SmokeBridge : IHostUiBridge
    {
        private bool _peers, _history;

        public TaskCompletionSource<UiPageSchema> SchemaReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TablesPushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnPluginMenusChanged() { }

        public void OnPluginSchemaUpdated(string pluginId, string menuId, UiPageSchema schema)
        {
            if (pluginId == PluginId && menuId == "main") SchemaReady.TrySetResult(schema);
        }

        public void OnPluginValuesUpdated(string pluginId, string menuId, Dictionary<string, JsonElement> values)
        {
            _peers |= values.ContainsKey("peers_table");
            _history |= values.ContainsKey("history_table");
            if (_peers && _history) TablesPushed.TrySetResult(true);
        }

        public void OnPluginNavigateRequested(string pluginId, string menuId) { }

        public Task<UiGetViewportHostResult> GetViewportHostAsync(string pluginId, string menuId) =>
            Task.FromResult(new UiGetViewportHostResult());
    }

    private static string ResolvePluginRoot([CallerFilePath] string callerPath = "")
    {
        var envRoot = Environment.GetEnvironmentVariable("EASYTIER_SMOKE_PLUGIN_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot)) return envRoot;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", "dist"));
    }
}
