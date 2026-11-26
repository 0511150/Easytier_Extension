using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EasyTierExt.HostCore.Plugins;
using PeerSpeedTest.Engine;
using Xunit;

namespace EasyTierExt.Tests;

/// <summary>回环测速会占满 CPU，单独串行运行，避免干扰其他依赖时序的测试。</summary>
[CollectionDefinition(nameof(PeerSpeedTestEngineTests), DisableParallelization = true)]
public sealed class PeerSpeedTestSerialCollection;

[Collection(nameof(PeerSpeedTestEngineTests))]
public class PeerSpeedTestEngineTests
{
    private static (SpeedTestServer Server, int Port) StartServer(Func<ConsentRequest, CancellationToken, Task<bool>> consent)
    {
        var server = new SpeedTestServer(consent, port: 0);
        server.SetBindAddresses([IPAddress.Loopback]);
        int port = server.GetBoundPort(IPAddress.Loopback);
        Assert.True(port > 0, server.LastBindError);
        return (server, port);
    }

    private static Task<bool> Accept(ConsentRequest _, CancellationToken __) => Task.FromResult(true);

    [Fact]
    public async Task AcceptedSession_PingUploadDownload_ProduceSampledResults()
    {
        ConsentRequest? asked = null;
        var (server, port) = StartServer((r, _) => { asked = r; return Task.FromResult(true); });
        await using var _ = server;
        await using var client = new SpeedTestClient(IPAddress.Loopback, port, IPAddress.Loopback);

        await client.RequestSessionAsync("tester", CancellationToken.None);
        Assert.Equal("tester", asked?.Hostname);
        Assert.Equal(IPAddress.Loopback, asked?.RemoteIp);
        Assert.Equal("tester", server.CurrentSession?.Hostname);

        var ping = await client.PingAsync(5, 10, CancellationToken.None);
        Assert.Equal(5, ping.Received);

        var samples = new List<SpeedSample>();
        var up = await client.UploadAsync(2, 1_500, samples.Add, CancellationToken.None);
        Assert.True(up.AverageBps > 1e6, $"upload {up.AverageBps} bps");
        Assert.True(up.TotalBytes > 0);
        // 1.5 秒 / 100ms 约 15 个采样；整套测试并行时线程池可能延迟计时器，只检查上限
        Assert.InRange(up.Samples.Count, 1, 20);
        Assert.Equal(up.Samples.Count, samples.Count);
        Assert.Equal(1.5, up.Samples[^1].Seconds, 2);

        var down = await client.DownloadAsync(2, 1_500, null, CancellationToken.None);
        Assert.True(down.AverageBps > 1e6, $"download {down.AverageBps} bps");
        Assert.True(down.PeakBps >= down.AverageBps * 0.5);

        // 发起方关闭控制连接后会话结束
        await client.DisposeAsync();
        for (int i = 0; i < 100 && server.CurrentSession is not null; i++) await Task.Delay(20);
        Assert.Null(server.CurrentSession);
    }

    [Fact]
    public async Task RejectedRequest_ReportsRejection_AndAllowsNextRequest()
    {
        bool allow = false;
        var (server, port) = StartServer((_, _) => Task.FromResult(allow));
        await using var _ = server;

        await using (var client = new SpeedTestClient(IPAddress.Loopback, port))
        {
            var ex = await Assert.ThrowsAsync<SpeedTestException>(() => client.RequestSessionAsync("a", CancellationToken.None));
            Assert.Contains("拒绝", ex.Message);
        }
        Assert.Null(server.CurrentSession);

        allow = true;
        await using var next = new SpeedTestClient(IPAddress.Loopback, port);
        await next.RequestSessionAsync("b", CancellationToken.None);
        Assert.Equal("b", server.CurrentSession?.Hostname);
    }

    [Fact]
    public async Task SecondRequester_IsBusy_WhileSessionActive()
    {
        var (server, port) = StartServer(Accept);
        await using var _ = server;
        await using var first = new SpeedTestClient(IPAddress.Loopback, port);
        await first.RequestSessionAsync("first", CancellationToken.None);

        await using var second = new SpeedTestClient(IPAddress.Loopback, port);
        var ex = await Assert.ThrowsAsync<SpeedTestException>(() => second.RequestSessionAsync("second", CancellationToken.None));
        Assert.Contains("正在测速", ex.Message);
    }

    [Fact]
    public async Task RequesterGivingUp_WithdrawsConsentPrompt()
    {
        var prompted = new TaskCompletionSource();
        var withdrawn = new TaskCompletionSource();
        var (server, port) = StartServer(async (_, ct) =>
        {
            prompted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { withdrawn.TrySetResult(); }
            return false;
        });
        await using var _ = server;

        using var cts = new CancellationTokenSource();
        var client = new SpeedTestClient(IPAddress.Loopback, port);
        var request = client.RequestSessionAsync("impatient", cts.Token);
        await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await withdrawn.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AbortSession_StopsRunningDownload()
    {
        var (server, port) = StartServer(Accept);
        await using var _ = server;
        await using var client = new SpeedTestClient(IPAddress.Loopback, port);
        await client.RequestSessionAsync("x", CancellationToken.None);

        var sw = Stopwatch.StartNew();
        var download = client.DownloadAsync(1, 10_000, null, CancellationToken.None);
        await Task.Delay(500);
        server.AbortSession();

        // 中止后要么抛出连接中断，要么立刻以已收到的数据结束，绝不能跑满 10 秒
        try { await download; } catch (SpeedTestException) { }
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"took {sw.ElapsedMilliseconds} ms");
        for (int i = 0; i < 100 && server.CurrentSession is not null; i++) await Task.Delay(20);
        Assert.Null(server.CurrentSession);
    }

    [Fact]
    public async Task DataConnection_WithoutValidToken_IsRefused()
    {
        var (server, port) = StartServer(Accept);
        await using var _ = server;

        async Task<SpeedReply?> SendAsync(SpeedRequest request)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await SpeedProtocol.WriteLineAsync(tcp.GetStream(), request, CancellationToken.None);
            return await SpeedProtocol.ReadLineAsync<SpeedReply>(tcp.GetStream(), CancellationToken.None);
        }

        var noToken = await SendAsync(new SpeedRequest { Op = SpeedProtocol.OpDownload, DurationMs = 1_000 });
        Assert.Equal(SpeedProtocol.ErrorUnauthorized, noToken?.Error);

        var unknown = await SendAsync(new SpeedRequest { Op = "format_disk" });
        Assert.Equal(SpeedProtocol.ErrorUnsupported, unknown?.Error);
    }

    [Fact]
    public async Task Sampler_AveragesOnlyTheLastWindow()
    {
        // 前 600ms 不产生流量，之后每秒 1,000,000 字节（8 Mbps）；结果只看最后 400ms
        var sw = Stopwatch.StartNew();
        long Total() => (long)(Math.Max(0, sw.Elapsed.TotalMilliseconds - 600) * 1_000);
        var never = new TaskCompletionSource().Task;

        var result = await SpeedTestClient.SampleAsync(never, Total, durationMs: 1_000, windowMs: 400, null, CancellationToken.None);

        Assert.InRange(result.AverageBps, 6e6, 10e6);
        Assert.Equal(1.0, result.Samples[^1].Seconds, 2);
        if (result.Samples[0].Seconds < 0.5) Assert.True(result.Samples[0].Bps < 1e6, "first sample should be idle");
    }

    [Theory]
    [InlineData("1.1.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.1.1", false)]
    [InlineData("2", "1.9.9", false)]
    [InlineData("", "1.0.0", true)]
    [InlineData("1.2.0", "dev", true)]
    public void HostVersionGate(string min, string host, bool expected)
    {
        Assert.Equal(expected, MarketplaceService.IsHostVersionSatisfied(min, host));
    }
}
