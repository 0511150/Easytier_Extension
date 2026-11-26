using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerSpeedTest.Engine;

/// <summary>
/// 测速协议 v2（TCP，单行 JSON 握手 + 原始数据）：
/// 1. request：发起方先建立控制连接并发送 hostname，被测方弹出确认；同意后回复 token，控制连接保持到测速结束。
///    控制连接断开或被测方中止时，该 token 下的全部连接立即关闭。
/// 2. ping / download / upload：数据连接必须携带有效 token，且来源 IP 与控制连接一致。
///    - ping：16 字节帧原样回显，直到发起方关闭连接；
///    - download：被测方持续发送 duration_ms 毫秒；
///    - upload：发起方持续发送，关闭发送方向后被测方回复收到的字节数。
/// </summary>
public static class SpeedProtocol
{
    public const int DefaultPort = 39700;
    public const string Magic = "ETSPEED";
    public const int Version = 2;

    public const string OpRequest = "request";
    public const string OpPing = "ping";
    public const string OpDownload = "download";
    public const string OpUpload = "upload";

    public const string ErrorBusy = "busy";
    public const string ErrorRejected = "rejected";
    public const string ErrorTimeout = "timeout";
    public const string ErrorUnauthorized = "unauthorized";
    public const string ErrorUnsupported = "unsupported_op";

    /// <summary>每个方向固定测试 15 秒。</summary>
    public const int PhaseDurationMs = 15_000;
    /// <summary>结果取每个方向最后 5 秒的平均速率。</summary>
    public const int AverageWindowMs = 5_000;
    public const int SampleIntervalMs = 100;
    public const int ConsentTimeoutMs = 30_000;

    public const int MinDurationMs = 1_000;
    public const int MaxDurationMs = 30_000;
    public const int BufferSize = 128 * 1024;
    public const int PingFrameSize = 16;
    public const int MaxLineBytes = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
}

public sealed class SpeedReply
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("v")] public int? Version { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("ms")] public double? Ms { get; set; }
}

/// <summary>被测方收到的测速请求。</summary>
public sealed record ConsentRequest(string Hostname, System.Net.IPAddress RemoteIp);

public sealed class SpeedTestException(string message, Exception? inner = null) : Exception(message, inner);
