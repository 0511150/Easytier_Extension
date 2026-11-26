using System.Runtime.CompilerServices;
using System.Text.Json;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Discovery;
using EasyTierExt.HostCore.Native;
using EasyTierExt.HostCore.Plugins;
using Xunit;

namespace EasyTierExt.Tests;

public class PublishedClipboardShareSmokeTest
{
    private const string PluginId = "clipboard-share";

    [Fact]
    public async Task PublishedClipboardShareExe_Connects_RegistersSchemaAndLists_And_StopsCleanly()
    {
        var root = ResolvePluginRoot();
        if (!File.Exists(Path.Combine(root, "plugins", PluginId, "ClipboardShare.exe")))
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
            var items = schema.Sections.SelectMany(s => s.Items).ToList();
            Assert.Contains(items, i => i.Key == "devices_table" && i.Type == "table");
            Assert.Contains(items, i => i.Key == "history" && i.Type == "card_list");
            Assert.Contains(items, i => i.Key == "ttl" && i.Type == "select");
            Assert.Contains(items, i => i.Key == "sync_enabled" && i.Type == "switch");

            // 启动后会推送一次设备表与历史
            Assert.True(await bridge.ListsPushed.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            await pm.StopPluginAsync(PluginId);
            var dataDir = Path.Combine(tempDataRoot, "plugins_data", PluginId);
            Assert.True(File.Exists(Path.Combine(dataDir, "identity.json")));
            // 历史只在内存中，不写入磁盘
            Assert.DoesNotContain(Directory.GetFiles(dataDir), f => Path.GetFileName(f).Contains("history", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, true); } catch { }
        }
    }

    private sealed class SmokeBridge : IHostUiBridge
    {
        private bool _devices, _history;

        public TaskCompletionSource<UiPageSchema> SchemaReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ListsPushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnPluginMenusChanged() { }

        public void OnPluginSchemaUpdated(string pluginId, string menuId, UiPageSchema schema)
        {
            if (pluginId == PluginId && menuId == "main") SchemaReady.TrySetResult(schema);
        }

        public void OnPluginValuesUpdated(string pluginId, string menuId, Dictionary<string, JsonElement> values)
        {
            lock (this)
            {
                _devices |= values.ContainsKey("devices_table");
                _history |= values.ContainsKey("history");
                if (_devices && _history) ListsPushed.TrySetResult(true);
            }
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
