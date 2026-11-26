using System.Net;
using System.Net.Sockets;
using PeerSpeedTest.Engine;
using Xunit;

namespace EasyTierExt.Tests;

public class PeerSpeedTestEngineTests
{
    private static async Task<(SpeedTestServer Server, int Port)> StartLoopbackServerAsync()
    {
        var server = new SpeedTestServer(port: 0);
        server.SetBindAddresses([IPAddress.Loopback]);
        int port = server.GetBoundPort(IPAddress.Loopback);
        Assert.True(port > 0, server.LastBindError);
        await Task.Yield();
        return (server, port);
    }

    [Fact]
    public async Task Loopback_PingDownloadUpload_AllProduceResults()
    {
        var (server, port) = await StartLoopbackServerAsync();
        await using var _ = server;
        var client = new SpeedTestClient(IPAddress.Loopback, port, IPAddress.Loopback);

        var ping = await client.PingAsync(5, 10, CancellationToken.None);
        Assert.Equal(5, ping.Received);
        Assert.Equal(0, ping.LossRate);
        Assert.True(ping.AvgMs >= 0 && ping.MaxMs >= ping.MinMs);

        var progress = new List<double>();
        double down = await client.DownloadAsync(2, 1_500, progress.Add, CancellationToken.None);
        Assert.True(down > 1, $"download {down} Mbps");
        Assert.NotEmpty(progress);

        double up = await client.UploadAsync(2, 1_500, null, CancellationToken.None);
        Assert.True(up > 1, $"upload {up} Mbps");

        // 所有连接结束后计数归零
        for (int i = 0; i < 50 && server.ActiveConnections > 0; i++) await Task.Delay(20);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact]
    public async Task Download_CanBeCancelled()
    {
        var (server, port) = await StartLoopbackServerAsync();
        await using var _ = server;
        var client = new SpeedTestClient(IPAddress.Loopback, port);

        using var cts = new CancellationTokenSource(400);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(1, 10_000, null, cts.Token));
        Assert.True(sw.ElapsedMilliseconds < 5_000);
    }

    [Fact]
    public async Task UnknownOperation_IsRejected()
    {
        var (server, port) = await StartLoopbackServerAsync();
        await using var _ = server;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await SpeedProtocol.WriteLineAsync(stream, new SpeedRequest { Op = "format_disk" }, CancellationToken.None);
        var reply = await SpeedProtocol.ReadLineAsync<SpeedReply>(stream, CancellationToken.None);
        Assert.NotNull(reply);
        Assert.False(reply.Ok);
        Assert.Equal("unsupported_op", reply.Error);
    }

    [Fact]
    public async Task RemovedBinding_RefusesConnections_WithFriendlyError()
    {
        var (server, port) = await StartLoopbackServerAsync();
        await using var _ = server;
        server.SetBindAddresses([]);
        Assert.Equal(0, server.ListeningCount);

        var client = new SpeedTestClient(IPAddress.Loopback, port);
        var ex = await Assert.ThrowsAsync<SpeedTestException>(() => client.PingAsync(1, 0, CancellationToken.None));
        Assert.Contains("拒绝", ex.Message);
    }

    [Theory]
    [InlineData(0, 1_000)]
    [InlineData(10_000, 10_000)]
    [InlineData(600_000, 30_000)]
    public void Duration_IsClamped(int requested, int expected)
    {
        Assert.Equal(expected, SpeedProtocol.ClampDuration(requested));
    }

    [Fact]
    public void Mbps_Conversion()
    {
        Assert.Equal(8.0, SpeedProtocol.ToMbps(1_000_000, 1_000), 6);
        Assert.Equal(0, SpeedProtocol.ToMbps(123, 0));
    }
}
