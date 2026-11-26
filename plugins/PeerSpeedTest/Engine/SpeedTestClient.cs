using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PeerSpeedTest.Engine;

public sealed record PingResult(int Sent, int Received, double AvgMs, double MinMs, double MaxMs, double JitterMs)
{
    public double LossRate => Sent == 0 ? 0 : 1.0 - (double)Received / Sent;
}

/// <summary>一次采样：相对本方向开始的秒数，以及该采样间隔内的速率（bit/s）。</summary>
public readonly record struct SpeedSample(double Seconds, double Bps);

/// <summary>单个方向的测速结果。</summary>
public sealed class PhaseResult
{
    public List<SpeedSample> Samples { get; } = [];
    public long TotalBytes { get; set; }
    /// <summary>最后 <see cref="SpeedProtocol.AverageWindowMs"/> 毫秒的平均速率（bit/s）。</summary>
    public double AverageBps { get; set; }
    public double PeakBps => Samples.Count == 0 ? 0 : Samples.Max(s => s.Bps);
}

/// <summary>
/// 测速客户端。传入本机虚拟 IP 作为源地址，确保流量走对应的 EasyTier 虚拟网卡。
/// 先 <see cref="RequestSessionAsync"/> 取得对方同意，再进行延迟、上传、下载测试。
/// </summary>
public sealed class SpeedTestClient(IPAddress target, int port, IPAddress? localBind = null) : IAsyncDisposable
{
    private const int ConnectTimeoutMs = 5_000;
    private const int PingTimeoutMs = 2_000;
    private const int UploadChunk = 32 * 1024;

    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(SpeedProtocol.BufferSize);

    private Connection? _control;
    private string? _token;

    /// <summary>请求对方同意测速，最多等待 <see cref="SpeedProtocol.ConsentTimeoutMs"/>。控制连接保持到 Dispose。</summary>
    public async Task RequestSessionAsync(string hostname, CancellationToken ct)
    {
        var request = new SpeedRequest { Op = SpeedProtocol.OpRequest, Hostname = hostname };
        var (connection, reply) = await OpenAsync(request, SpeedProtocol.ConsentTimeoutMs + 10_000, ct);
        if (string.IsNullOrEmpty(reply.Token))
        {
            connection.Dispose();
            throw new SpeedTestException("对方未返回会话凭据");
        }
        _control = connection;
        _token = reply.Token;
    }

    public async Task<PingResult> PingAsync(int count, int intervalMs, CancellationToken ct)
    {
        var (c, _) = await OpenAsync(NewDataRequest(SpeedProtocol.OpPing, 0), ConnectTimeoutMs, ct);
        using var connection = c;
        var stream = connection.Stream;

        var frame = new byte[SpeedProtocol.PingFrameSize];
        var echo = new byte[SpeedProtocol.PingFrameSize];
        var rtts = new List<double>(count);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(intervalMs, ct);
            BinaryPrimitives.WriteInt64LittleEndian(frame, i);
            long start = Stopwatch.GetTimestamp();
            BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(8), start);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PingTimeoutMs);
            try
            {
                await stream.WriteAsync(frame, timeout.Token);
                await stream.ReadExactlyAsync(echo, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break; // TCP 上超时说明链路已中断，后续都记为丢失
            }
            catch (IOException)
            {
                break;
            }

            if (BinaryPrimitives.ReadInt64LittleEndian(echo) != i)
                throw new SpeedTestException("延迟测试数据不一致");
            rtts.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        if (rtts.Count == 0) return new PingResult(count, 0, 0, 0, 0, 0);
        double jitter = 0;
        for (int i = 1; i < rtts.Count; i++) jitter += Math.Abs(rtts[i] - rtts[i - 1]);
        if (rtts.Count > 1) jitter /= rtts.Count - 1;
        return new PingResult(count, rtts.Count, rtts.Average(), rtts.Min(), rtts.Max(), jitter);
    }

    /// <summary>上传：本机持续发送 durationMs 毫秒，每 100ms 采样一次。</summary>
    public async Task<PhaseResult> UploadAsync(int streams, int durationMs, Action<SpeedSample>? onSample, CancellationToken ct)
    {
        durationMs = SpeedProtocol.ClampDuration(durationMs);
        var connections = await OpenManyAsync(SpeedProtocol.OpUpload, streams, durationMs, ct);
        using var stop = new CancellationTokenSource();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            long total = 0;
            var writers = connections.Select(c => Task.Run(async () =>
            {
                var chunk = Payload.AsMemory(0, UploadChunk);
                while (!stop.IsCancellationRequested)
                {
                    await c.Stream.WriteAsync(chunk, abort.Token);
                    Interlocked.Add(ref total, chunk.Length);
                }
                // 正常结束：关闭发送方向并等对方确认收完，避免连接被直接重置
                c.Socket.Shutdown(SocketShutdown.Send);
                using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                replyTimeout.CancelAfter(5_000);
                try { await SpeedProtocol.ReadLineAsync<SpeedReply>(c.Stream, replyTimeout.Token); } catch { }
            })).ToList();

            var result = await SampleAsync(Task.WhenAll(writers), () => Interlocked.Read(ref total), durationMs, SpeedProtocol.AverageWindowMs, onSample, ct);
            stop.Cancel();
            await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(10), ct);
            return result;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException && !ct.IsCancellationRequested)
        {
            throw new SpeedTestException("上传过程中连接中断（对方可能已中止测速）", ex);
        }
        finally
        {
            abort.Cancel();
            foreach (var c in connections) c.Dispose();
        }
    }

    /// <summary>下载：对方持续发送 durationMs 毫秒，每 100ms 采样一次。</summary>
    public async Task<PhaseResult> DownloadAsync(int streams, int durationMs, Action<SpeedSample>? onSample, CancellationToken ct)
    {
        durationMs = SpeedProtocol.ClampDuration(durationMs);
        var connections = await OpenManyAsync(SpeedProtocol.OpDownload, streams, durationMs, ct);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            long total = 0;
            var readers = connections.Select(c => Task.Run(async () =>
            {
                var buffer = new byte[SpeedProtocol.BufferSize];
                while (true)
                {
                    int n = await c.Stream.ReadAsync(buffer, abort.Token);
                    if (n == 0) return;
                    Interlocked.Add(ref total, n);
                }
            })).ToList();

            // 对方按自己的时钟发送，本机只统计前 durationMs 毫秒，之后的尾包不计
            return await SampleAsync(Task.WhenAll(readers), () => Interlocked.Read(ref total), durationMs, SpeedProtocol.AverageWindowMs, onSample, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException && !ct.IsCancellationRequested)
        {
            throw new SpeedTestException("下载过程中连接中断（对方可能已中止测速）", ex);
        }
        finally
        {
            abort.Cancel();
            foreach (var c in connections) c.Dispose();
        }
    }

    /// <summary>
    /// 每 <see cref="SpeedProtocol.SampleIntervalMs"/> 采样累计字节数，直到 durationMs 或传输提前结束；
    /// 结果均值取最后 <see cref="SpeedProtocol.AverageWindowMs"/> 毫秒。
    /// </summary>
    public static async Task<PhaseResult> SampleAsync(Task work, Func<long> readTotal, int durationMs, int windowMs, Action<SpeedSample>? onSample, CancellationToken ct)
    {
        var result = new PhaseResult();
        double duration = durationMs / 1000.0;
        double windowStart = Math.Max(0, duration - windowMs / 1000.0);
        var sw = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SpeedProtocol.SampleIntervalMs));

        long lastBytes = 0, windowBytes = -1;
        double lastT = 0, windowT = 0;
        while (true)
        {
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            await Task.WhenAny(tick, work);
            ct.ThrowIfCancellationRequested();
            if (work.IsFaulted) await work; // 传播读写异常

            // 速率按真实经过时间计算（计时器可能因系统繁忙延迟触发），只有显示用的横坐标截断到 duration
            double t = sw.Elapsed.TotalSeconds;
            long bytes = readTotal();
            if (t > lastT)
            {
                double bps = (bytes - lastBytes) * 8 / (t - lastT);
                var sample = new SpeedSample(Math.Round(Math.Min(t, duration), 2), bps);
                result.Samples.Add(sample);
                onSample?.Invoke(sample);
            }
            if (windowBytes < 0 && t >= windowStart)
            {
                windowBytes = bytes;
                windowT = t;
            }
            lastBytes = bytes;
            lastT = t;
            if (t >= duration || work.IsCompleted) break;
        }

        result.TotalBytes = lastBytes;
        result.AverageBps = windowBytes >= 0 && lastT - windowT > 0.05
            ? (lastBytes - windowBytes) * 8 / (lastT - windowT)
            : lastT > 0 ? lastBytes * 8 / lastT : 0;
        return result;
    }

    private SpeedRequest NewDataRequest(string op, int durationMs)
    {
        if (_token is null) throw new InvalidOperationException("尚未取得对方同意");
        return new SpeedRequest { Op = op, DurationMs = durationMs, Token = _token };
    }

    private async Task<List<Connection>> OpenManyAsync(string op, int streams, int durationMs, CancellationToken ct)
    {
        streams = Math.Clamp(streams, 1, 16);
        var request = NewDataRequest(op, durationMs);
        var tasks = Enumerable.Range(0, streams).Select(_ => OpenAsync(request, ConnectTimeoutMs, ct)).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            foreach (var t in tasks.Where(t => t.IsCompletedSuccessfully)) t.Result.Connection.Dispose();
            throw tasks.Select(t => t.Exception?.InnerException).FirstOrDefault(e => e is SpeedTestException)
                  ?? tasks.Select(t => t.Exception?.InnerException).FirstOrDefault(e => e is not null)
                  ?? new OperationCanceledException(ct);
        }
        return tasks.Select(t => t.Result.Connection).ToList();
    }

    private async Task<(Connection Connection, SpeedReply Reply)> OpenAsync(SpeedRequest request, int replyTimeoutMs, CancellationToken ct)
    {
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (localBind is not null) socket.Bind(new IPEndPoint(localBind, 0));

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeoutMs);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(target, port), timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new SpeedTestException("连接超时，请确认对方在线且防火墙已放行 TCP 39700");
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    throw new SpeedTestException("对方拒绝连接，可能未启用对端测速插件");
                }
                catch (SocketException ex)
                {
                    throw new SpeedTestException($"连接失败：{ex.SocketErrorCode}", ex);
                }
            }

            var stream = new NetworkStream(socket, ownsSocket: true);
            SpeedReply? reply;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(replyTimeoutMs);
                try
                {
                    await SpeedProtocol.WriteLineAsync(stream, request, timeout.Token);
                    reply = await SpeedProtocol.ReadLineAsync<SpeedReply>(stream, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    stream.Dispose();
                    throw new SpeedTestException(request.Op == SpeedProtocol.OpRequest ? "对方未响应测速请求" : "对方响应超时");
                }
            }

            if (reply is null)
            {
                stream.Dispose();
                throw new SpeedTestException(request.Op == SpeedProtocol.OpRequest ? "对方取消了测速请求" : "对方关闭了连接（测速可能已被中止）");
            }
            if (!reply.Ok)
            {
                stream.Dispose();
                throw new SpeedTestException(DescribeError(reply.Error));
            }
            return (new Connection(socket, stream), reply);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string DescribeError(string? error) => error switch
    {
        SpeedProtocol.ErrorRejected => "对方拒绝了测速请求",
        SpeedProtocol.ErrorTimeout => "对方 30 秒内未同意，请求已失效",
        SpeedProtocol.ErrorBusy => "对方正在测速中，请稍后再试",
        SpeedProtocol.ErrorUnauthorized => "测速会话已失效（对方可能已中止）",
        SpeedProtocol.ErrorUnsupported => "对方插件版本过旧，请让对方更新“对端测速”",
        _ => $"对方拒绝测速：{error}"
    };

    public ValueTask DisposeAsync()
    {
        _control?.Dispose();
        _control = null;
        _token = null;
        return ValueTask.CompletedTask;
    }

    private sealed record Connection(Socket Socket, NetworkStream Stream) : IDisposable
    {
        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { Socket.Dispose(); } catch { }
        }
    }
}
