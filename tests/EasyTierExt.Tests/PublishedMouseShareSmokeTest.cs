using System.Runtime.CompilerServices;
using System.Text.Json;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Discovery;
using EasyTierExt.HostCore.Native;
using EasyTierExt.HostCore.Plugins;
using Xunit;

namespace EasyTierExt.Tests;

public class PublishedMouseShareSmokeTest
{
    private const string PluginId = "mouse-share";

    [Fact]
    public async Task PublishedMouseShareExe_Connects_RegistersLayoutAndDevices_And_StopsCleanly()
    {
        var root = ResolvePluginRoot();
        if (!File.Exists(Path.Combine(root, "plugins", PluginId, "MouseShare.exe")))
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
            Assert.Contains(items, i => i.Key == "layout" && i.Type == "screen_layout");
            Assert.Contains(items, i => i.Key == "devices" && i.Type == "table");
            Assert.Contains(items, i => i.Key == "mon_scale" && i.Type == "select");

            // 启动后推送排列与设备表；有显示器时第一项是本机（CI 环境可能没有显示器）
            var layout = await bridge.LayoutPushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var screens = layout.GetProperty("screens");
            if (screens.GetArrayLength() > 0)
            {
                Assert.Equal("self", screens[0].GetProperty("tone").GetString());
            }

            await pm.StopPluginAsync(PluginId);
            var dataDir = Path.Combine(tempDataRoot, "plugins_data", PluginId);
            Assert.True(File.Exists(Path.Combine(dataDir, "settings.json")));
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, true); } catch { }
        }
    }

    private sealed class SmokeBridge : IHostUiBridge
    {
        private bool _devices;
        private JsonElement? _layout;

        public TaskCompletionSource<UiPageSchema> SchemaReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> LayoutPushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnPluginMenusChanged() { }

        public void OnPluginSchemaUpdated(string pluginId, string menuId, UiPageSchema schema)
        {
            if (pluginId == PluginId && menuId == "main") SchemaReady.TrySetResult(schema);
        }

        public void OnPluginValuesUpdated(string pluginId, string menuId, Dictionary<string, JsonElement> values)
        {
            _devices |= values.ContainsKey("devices");
            if (values.TryGetValue("layout", out var l)) _layout = l.Clone();
            if (_devices && _layout is { } layout) LayoutPushed.TrySetResult(layout);
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
