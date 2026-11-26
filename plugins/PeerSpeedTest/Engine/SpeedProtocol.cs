using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerSpeedTest.Engine;

/// <summary>
/// 测速协议（TCP，单行 JSON 握手 + 原始数据）：
/// 1. 客户端发送一行 <see cref="SpeedRequest"/>；服务端回复一行 <see cref="SpeedReply"/>（ok=false 时附带 error 并关闭）。
/// 2. ping：客户端发送 16 字节帧，服务端原样回显，直到客户端关闭连接。
/// 3. download：服务端连续发送随机数据 duration_ms 毫秒后关闭发送方向，客户端统计接收速率。
/// 4. upload：客户端连续发送 duration_ms 毫秒后关闭发送方向，服务端回复一行统计结果（预热后的 bytes / ms）。
/// </summary>
public static class SpeedProtocol
{
    public const int DefaultPort = 39700;
    public const string Magic = "ETSPEED";
    public const int Version = 1;

    public const string OpPing = "ping";
    public const string OpDownload = "download";
    public const string OpUpload = "upload";

    public const int MinDurationMs = 1_000;
    public const int MaxDurationMs = 30_000;
    public const int BufferSize = 128 * 1024;
    public const int PingFrameSize = 16;
    public const int MaxLineBytes = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>开头这段时间的数据不计入结果，避开 TCP 慢启动。</summary>
    public static int WarmupMs(int durationMs) => Math.Min(1_000, durationMs / 4);

    public static int ClampDuration(int durationMs) => Math.Clamp(durationMs, MinDurationMs, MaxDurationMs);

    public static double ToMbps(long bytes, double ms) => ms <= 0 ? 0 : bytes * 8.0 / ms / 1_000.0;

    public static async Task WriteLineAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var line = new byte[bytes.Length + 1];
        bytes.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        await stream.WriteAsync(line, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>逐字节读取一行，避免多读走后续的测速数据。连接关闭时返回 null。</summary>
    public static async Task<T?> ReadLineAsync<T>(Stream stream, CancellationToken ct) where T : class
    {
        var buffer = new byte[MaxLineBytes];
        var one = new byte[1];
        int length = 0;
        while (true)
        {
            int n = await stream.ReadAsync(one, ct);
            if (n == 0) return null;
            if (one[0] == (byte)'\n') break;
            if (length >= buffer.Length) throw new InvalidDataException("握手消息过长");
            buffer[length++] = one[0];
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("握手消息格式错误", ex);
        }
    }
}

public sealed class SpeedRequest
{
    [JsonPropertyName("magic")] public string Magic { get; set; } = SpeedProtocol.Magic;
    [JsonPropertyName("v")] public int Version { get; set; } = SpeedProtocol.Version;
    [JsonPropertyName("op")] public string Op { get; set; } = string.Empty;
    [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
}

public sealed class SpeedReply
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("v")] public int? Version { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("ms")] public double? Ms { get; set; }
}

public sealed class SpeedTestException(string message, Exception? inner = null) : Exception(message, inner);
