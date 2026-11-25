using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Discovery;
using EasyTierExt.HostCore.Native;
using EasyTierExt.HostCore.Plugins;
using Xunit;

namespace EasyTierExt.Tests;

public class PublishedScreenShareSmokeTest
{
    [Fact]
    public async Task PublishedScreenShareExe_Connects_RegistersUnifiedMainSchema_ResizesViewport_And_StopsCleanly()
    {
        var distRoot = ResolveDistRoot();
        var exePath = Path.Combine(distRoot, "plugins", "screen-share", "ScreenShare.exe");
        if (!File.Exists(exePath))
        {
            return;
        }

        IntPtr hostHwnd = CreateWindowExW(
            0x00000004,
            "STATIC",
            "",
            0x02000000 | 0x04000000,
            0, 0, 800, 600,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

        try
        {
            var tempDataRoot = Path.Combine(Path.GetTempPath(), $"et_smoke_data_{Guid.NewGuid():N}");
            using var job = new JobObjectManager();
            var networks = new List<HostNetworkStatus>();
            await using var discovery = new DiscoveryService(() => networks, _ => [], listenPort: 0);
            var bridge = new SmokeBridge(hostHwnd);
            var pipeName = $"et_smoke_{Guid.NewGuid():N}";
            await using var pm = new PluginManager(job, () => networks, _ => [], discovery, bridge, pipeName, tempDataRoot);
            pm.StartServer();

            var mkt = new MarketplaceService(distRoot);
            var installed = mkt.ScanInstalledPlugins(new Dictionary<string, bool> { ["screen-share"] = true });
            pm.SetInstalledPlugins(installed);

            await pm.StartPluginAsync("screen-share");
            var ready = await bridge.MainSchemaReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(ready);

            Assert.NotNull(pm.GetMenuSchema("screen-share", "main"));
            Assert.Null(pm.GetMenuSchema("screen-share", "viewer"));

            await pm.SendViewportChangedAsync("screen-share", new EventViewportChangedParams
            {
                MenuId = "main",
                Visible = true,
                Width = 640,
                Height = 360
            });
            await Task.Delay(300);
            await pm.SendViewportChangedAsync("screen-share", new EventViewportChangedParams
            {
                MenuId = "main",
                Visible = false,
                Width = 640,
                Height = 360
            });

            await pm.StopPluginAsync("screen-share");
        }
        finally
        {
            if (hostHwnd != IntPtr.Zero) DestroyWindow(hostHwnd);
        }
    }

    private sealed class SmokeBridge(IntPtr hostHwnd) : IHostUiBridge
    {
        public TaskCompletionSource<bool> MainSchemaReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnPluginMenusChanged() { }
        public void OnPluginSchemaUpdated(string pluginId, string menuId, UiPageSchema schema)
        {
            if (menuId == "main")
            {
                MainSchemaReady.TrySetResult(true);
            }
        }
        public void OnPluginValuesUpdated(string pluginId, string menuId, Dictionary<string, JsonElement> values) { }
        public void OnPluginNavigateRequested(string pluginId, string menuId) { }
        public Task<UiGetViewportHostResult> GetViewportHostAsync(string pluginId, string menuId) =>
            Task.FromResult(new UiGetViewportHostResult { Hwnd = hostHwnd.ToInt64(), Width = 800, Height = 600 });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private static string ResolveDistRoot([CallerFilePath] string callerPath = "")
    {
        var envRoot = Environment.GetEnvironmentVariable("EASYTIER_SMOKE_PLUGIN_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
        {
            return envRoot;
        }
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", "dist"));
    }
}
