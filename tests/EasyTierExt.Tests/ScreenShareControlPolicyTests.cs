using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ScreenShare.Streaming;
using Xunit;

namespace EasyTierExt.Tests;

public class ScreenShareControlPolicyTests
{
    [Fact]
    public async Task RemoteControl_RequiresExplicitOptIn_And_RevokesWhenDisabled()
    {
        var settings = new ShareSettings { AllowRemoteControl = false };
        await using var server = new ScreenShareServer(settings);
        server.Start(["127.0.0.1"]);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", ScreenShareServer.ControlPort);
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "hello", viewer_name = "test", udp_port = 49999 }));
        var welcomeLine = await reader.ReadLineAsync();
        Assert.NotNull(welcomeLine);
        using var welcomeDoc = JsonDocument.Parse(welcomeLine!);
        Assert.False(welcomeDoc.RootElement.GetProperty("allow_control").GetBoolean());

        // 1. Cannot acquire control while AllowRemoteControl == false
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "acquire_control" }));
        var res1 = await reader.ReadLineAsync();
        Assert.NotNull(res1);
        using var res1Doc = JsonDocument.Parse(res1!);
        Assert.Equal("control_result", res1Doc.RootElement.GetProperty("type").GetString());
        Assert.False(res1Doc.RootElement.GetProperty("granted").GetBoolean());

        // 2. Enable opt-in ("开启被控") -> viewer receives policy update and can acquire control
        settings.AllowRemoteControl = true;
        await server.UpdateControlPolicyAsync();
        var pol1 = await reader.ReadLineAsync();
        Assert.NotNull(pol1);
        using var pol1Doc = JsonDocument.Parse(pol1!);
        Assert.Equal("control_policy", pol1Doc.RootElement.GetProperty("type").GetString());
        Assert.True(pol1Doc.RootElement.GetProperty("allow_control").GetBoolean());

        await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "acquire_control" }));
        var res2 = await reader.ReadLineAsync();
        Assert.NotNull(res2);
        using var res2Doc = JsonDocument.Parse(res2!);
        Assert.True(res2Doc.RootElement.GetProperty("granted").GetBoolean());

        // 3. Disable opt-in -> server pushes control_policy(false) and revokes control_result(false)
        settings.AllowRemoteControl = false;
        await server.UpdateControlPolicyAsync();
        var pol2 = await reader.ReadLineAsync();
        var rev = await reader.ReadLineAsync();
        Assert.NotNull(pol2);
        Assert.NotNull(rev);
        using var pol2Doc = JsonDocument.Parse(pol2!);
        using var revDoc = JsonDocument.Parse(rev!);
        Assert.False(pol2Doc.RootElement.GetProperty("allow_control").GetBoolean());
        Assert.False(revDoc.RootElement.GetProperty("granted").GetBoolean());
    }
}
