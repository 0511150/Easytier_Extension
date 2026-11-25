using System.IO.Pipes;
using System.Text.Json;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using EasyTierExt.HostCore.Discovery;
using EasyTierExt.HostCore.Native;
using EasyTierExt.HostCore.Plugins;
using Xunit;

namespace EasyTierExt.Tests;

public class JsonRpcPipeTests
{
    [Fact]
    public async Task PluginHandshake_Schema_UpdateValues_And_UiAction_RoundTrip()
    {
        using var job = new JobObjectManager();
        var networks = new List<HostNetworkStatus>
        {
            new() { NetworkId = "net1", NetworkName = "test", DisplayName = "测试", Running = true, Ipv4 = "10.144.144.1" }
        };
        await using var discovery = new DiscoveryService(() => networks, _ => [], listenPort: 0);
        var bridge = new FakeBridge();
        var pipeName = $"et_test_pipe_{Guid.NewGuid():N}";
        await using var pm = new PluginManager(job, () => networks, _ => [], discovery, bridge, pipeName);
        pm.StartServer();
        pm.RegisterExpectedTokenForExternalClient("demo", "secret-tok");

        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(3000);
        await using var conn = new JsonRpcPipeConnection(clientPipe);
        var receivedAction = new TaskCompletionSource<EventUiActionParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Start(null, (method, @params) =>
        {
            if (method == IpcMethods.EventUiAction && @params is not null)
            {
                receivedAction.TrySetResult(@params.Value.Deserialize<EventUiActionParams>(IpcJsonContext.Options)!);
            }
            return Task.CompletedTask;
        });

        var hello = await conn.SendRequestAsync<PluginHelloResult>(IpcMethods.PluginHello, new PluginHelloParams
        {
            PluginId = "demo",
            Token = "secret-tok"
        });
        Assert.Equal("1.0.0", hello?.HostVersion);

        var nets = await conn.SendRequestAsync<List<HostNetworkStatus>>(IpcMethods.HostGetNetworks);
        Assert.Single(nets!);
        Assert.Equal("10.144.144.1", nets![0].Ipv4);

        await conn.SendRequestAsync<object>(IpcMethods.UiSetSchema, new UiSetSchemaParams
        {
            MenuId = "main",
            Schema = new UiPageSchema { Sections = [new UiSection { Id = "s1", Title = "S1" }] }
        });
        Assert.NotNull(pm.GetMenuSchema("demo", "main"));

        await conn.SendRequestAsync<object>(IpcMethods.UiUpdateValues, new UiUpdateValuesParams
        {
            MenuId = "main",
            Values = new Dictionary<string, JsonElement> { ["k1"] = JsonSerializer.SerializeToElement("v1") }
        });
        Assert.Equal("v1", pm.GetMenuValues("demo", "main")!["k1"].GetString());

        await pm.SendUiActionAsync("demo", new EventUiActionParams { MenuId = "main", Key = "btn", Action = "click" });
        var act = await receivedAction.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("btn", act.Key);
    }

    private sealed class FakeBridge : IHostUiBridge
    {
        public void OnPluginMenusChanged() { }
        public void OnPluginSchemaUpdated(string pluginId, string menuId, UiPageSchema schema) { }
        public void OnPluginValuesUpdated(string pluginId, string menuId, Dictionary<string, JsonElement> values) { }
        public void OnPluginNavigateRequested(string pluginId, string menuId) { }
        public Task<UiGetViewportHostResult> GetViewportHostAsync(string pluginId, string menuId) =>
            Task.FromResult(new UiGetViewportHostResult { Hwnd = 12345, Width = 800, Height = 600 });
    }
}
