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

/// <summary>
/// 测速客户端。传入本机虚拟 IP 作为源地址，确保流量走对应的 EasyTier 虚拟网卡。
/// </summary>
public sealed class SpeedTestClient(IPAddress target, int port, IPAddress? localBind = null)
{
    private const int ConnectTimeoutMs = 5_000;
    private const int PingTimeoutMs = 2_000;
    private const int ProgressIntervalMs = 500;

    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(SpeedProtocol.BufferSize);

    public async Task<PingResult> PingAsync(int count, int intervalMs, CancellationToken ct)
    {
        var (socket, stream) = await OpenAsync(SpeedProtocol.OpPing, 0, ct);
        await using var _ = stream;
        using var socketGuard = socket;

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
            catch (Exception ex) when (ex is IOException or EndOfStreamException)
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

    /// <summary>下载测试：对方发送、本机接收，返回预热后的平均 Mbps。</summary>
    public async Task<double> DownloadAsync(int streams, int durationMs, Action<double>? onProgressMbps, CancellationToken ct)
    {
        durationMs = SpeedProtocol.ClampDuration(durationMs);
        var connections = await OpenManyAsync(SpeedProtocol.OpDownload, streams, durationMs, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(durationMs + 10_000);
        try
        {
            long total = 0;
            var readers = connections.Select(c => Task.Run(async () =>
            {
                var buffer = new byte[SpeedProtocol.BufferSize];
                while (true)
                {
                    int n = await c.Stream.ReadAsync(buffer, deadline.Token);
                    if (n == 0) return;
                    Interlocked.Add(ref total, n);
                }
            })).ToList();

            var meter = new Meter(SpeedProtocol.WarmupMs(durationMs));
            await meter.RunAsync(Task.WhenAll(readers), () => Interlocked.Read(ref total), onProgressMbps, ct);
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref total) == 0) throw new SpeedTestException("没有收到任何数据");
            return meter.ResultMbps(Interlocked.Read(ref total));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpeedTestException("下载测试超时");
        }
        finally
        {
            foreach (var c in connections) c.Dispose();
        }
    }

    /// <summary>上传测试：本机发送、对方接收，以对方统计的接收量为准，返回预热后的平均 Mbps。</summary>
    public async Task<double> UploadAsync(int streams, int durationMs, Action<double>? onProgressMbps, CancellationToken ct)
    {
        durationMs = SpeedProtocol.ClampDuration(durationMs);
        var connections = await OpenManyAsync(SpeedProtocol.OpUpload, streams, durationMs, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(durationMs + 15_000);
        try
        {
            long sentTotal = 0;
            var sw = Stopwatch.StartNew();
            var writers = connections.Select(c => Task.Run(async () =>
            {
                while (sw.ElapsedMilliseconds < durationMs)
                {
                    await c.Stream.WriteAsync(Payload, deadline.Token);
                    Interlocked.Add(ref sentTotal, Payload.Length);
                }
                c.Socket.Shutdown(SocketShutdown.Send);
                var reply = await SpeedProtocol.ReadLineAsync<SpeedReply>(c.Stream, deadline.Token);
                if (reply is not { Ok: true, Bytes: not null, Ms: not null })
                    throw new SpeedTestException("对方未返回上传统计");
                return SpeedProtocol.ToMbps(reply.Bytes.Value, reply.Ms.Value);
            })).ToList();

            // 进度按本机发出量估算，最终结果以对方实际收到的为准
            var meter = new Meter(SpeedProtocol.WarmupMs(durationMs));
            var all = Task.WhenAll(writers);
            await meter.RunAsync(all, () => Interlocked.Read(ref sentTotal), onProgressMbps, ct);
            ct.ThrowIfCancellationRequested();
            return (await all).Sum();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpeedTestException("上传测试超时");
        }
        finally
        {
            foreach (var c in connections) c.Dispose();
        }
    }

    private async Task<List<Connection>> OpenManyAsync(string op, int streams, int durationMs, CancellationToken ct)
    {
        streams = Math.Clamp(streams, 1, 16);
        var tasks = Enumerable.Range(0, streams).Select(_ => OpenAsync(op, durationMs, ct)).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            foreach (var t in tasks.Where(t => t.IsCompletedSuccessfully)) t.Result.Dispose();
            throw tasks.Select(t => t.Exception?.InnerException).FirstOrDefault(e => e is SpeedTestException)
                  ?? tasks.Select(t => t.Exception?.InnerException).FirstOrDefault(e => e is not null)
                  ?? new OperationCanceledException(ct);
        }
        return tasks.Select(t => t.Result).ToList();
    }

    private async Task<Connection> OpenAsync(string op, int durationMs, CancellationToken ct)
    {
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (localBind is not null) socket.Bind(new IPEndPoint(localBind, 0));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, port), timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SpeedTestException("连接超时，请确认对方在线且防火墙已放行");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                throw new SpeedTestException("对方拒绝连接，可能已关闭“允许他人对我测速”");
            }
            catch (SocketException ex)
            {
                throw new SpeedTestException($"连接失败：{ex.SocketErrorCode}", ex);
            }

            var stream = new NetworkStream(socket, ownsSocket: true);
            await SpeedProtocol.WriteLineAsync(stream, new SpeedRequest { Op = op, DurationMs = durationMs }, timeout.Token);
            var reply = await SpeedProtocol.ReadLineAsync<SpeedReply>(stream, timeout.Token)
                        ?? throw new SpeedTestException("对方关闭了连接");
            if (!reply.Ok)
            {
                stream.Dispose();
                throw new SpeedTestException(reply.Error == "busy" ? "对方正忙（同时测速的连接过多）" : $"对方拒绝测速：{reply.Error}");
            }
            return new Connection(socket, stream);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed record Connection(Socket Socket, NetworkStream Stream) : IDisposable
    {
        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { Socket.Dispose(); } catch { }
        }
    }

    /// <summary>定时采样累计字节数：上报实时速率，并记录预热结束时的位置。</summary>
    private sealed class Meter(int warmupMs)
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _bytesAtWarmup = -1;
        private double _msAtWarmup;
        private double _endMs;

        public async Task RunAsync(Task work, Func<long> readTotal, Action<double>? onProgress, CancellationToken ct)
        {
            long lastBytes = 0;
            double lastMs = 0;
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, Task.Delay(ProgressIntervalMs / 2, CancellationToken.None));
                double now = _sw.Elapsed.TotalMilliseconds;
                long bytes = readTotal();
                if (_bytesAtWarmup < 0 && now >= warmupMs)
                {
                    _bytesAtWarmup = bytes;
                    _msAtWarmup = now;
                }
                if (now - lastMs >= ProgressIntervalMs)
                {
                    onProgress?.Invoke(SpeedProtocol.ToMbps(bytes - lastBytes, now - lastMs));
                    lastBytes = bytes;
                    lastMs = now;
                }
            }
            _endMs = _sw.Elapsed.TotalMilliseconds;
            await work; // 传播读写异常
        }

        public double ResultMbps(long total) =>
            _bytesAtWarmup >= 0 && _endMs > _msAtWarmup
                ? SpeedProtocol.ToMbps(total - _bytesAtWarmup, _endMs - _msAtWarmup)
                : SpeedProtocol.ToMbps(total, _endMs);
    }
}
