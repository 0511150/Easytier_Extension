using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipboardShare.Engine;

/// <summary>
/// 剪贴板同步协议 v1（TCP，每帧 = 4 字节小端长度 + UTF-8 JSON）。每条连接单向发送剪贴板：
/// 1. 接收方先发 challenge（自身设备 ID、随机 nonce、时钟）；
/// 2. 发送方回 hello：已配对时带 HMAC 证明，首次配对时带新生成的共享密钥 pair_key（接收方需用户同意）；
/// 3. 接收方回 welcome（已有记录的 Id 与时间）或 error；
/// 4. 发送方先补发对方缺少的记录（merge=true）并以 sync_done 结束，之后复制一条发一条，每 10 秒 ping 一次。
/// </summary>
public static class ClipProtocol
{
    public const int DefaultPort = 39710;
    public const string Magic = "ETCLIP";
    public const int Version = 1;

    /// <summary>单条剪贴板内容（文字 UTF-8 或图片 PNG）上限。</summary>
    public const int MaxClipBytes = 20 * 1024 * 1024;
    public const int MaxFrameBytes = 48 * 1024 * 1024;
    public const int KeyBytes = 32;

    public const int ConsentTimeoutMs = 60_000;
    public const int HandshakeTimeoutMs = 10_000;
    public const int ConnectTimeoutMs = 5_000;
    public const int PingIntervalMs = 10_000;
    /// <summary>超过该时长没有收到对方任何消息即视为掉线。</summary>
    public const int IdleTimeoutMs = 30_000;

    public const string TypeChallenge = "challenge";
    public const string TypeHello = "hello";
    public const string TypeWelcome = "welcome";
    public const string TypeError = "error";
    public const string TypeClip = "clip";
    public const string TypeSyncDone = "sync_done";
    public const string TypePing = "ping";
    public const string TypePong = "pong";
    public const string TypeOk = "ok";

    public const string ErrorRejected = "rejected";
    public const string ErrorTimeout = "timeout";
    public const string ErrorBusy = "busy";
    public const string ErrorConflict = "conflict";
    public const string ErrorUnpaired = "unpaired";
    public const string ErrorPaused = "paused";
    public const string ErrorMismatch = "mismatch";
    public const string ErrorVersion = "version";

    public const string KindText = "text";
    public const string KindImage = "image";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long ToUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public static async Task WriteAsync(Stream stream, ClipMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (json.Length > MaxFrameBytes) throw new InvalidDataException("消息过大");
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>读取一帧；连接正常关闭时返回 null。</summary>
    public static async Task<ClipMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await stream.ReadAsync(header.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0) return null;
                throw new EndOfStreamException();
            }
            read += n;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("消息长度无效");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        try
        {
            return JsonSerializer.Deserialize<ClipMessage>(body, JsonOptions) ?? throw new InvalidDataException("消息为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("消息格式错误", ex);
        }
    }

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>发送方证明自己持有配对密钥：HMAC-SHA256(key, nonce | 发送方 ID | 接收方 ID)。</summary>
    public static string ComputeProof(byte[] key, string nonce, string senderId, string receiverId) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{nonce}|{senderId}|{receiverId}")));

    public static bool VerifyProof(byte[] key, string nonce, string senderId, string receiverId, string? proof)
    {
        if (string.IsNullOrEmpty(proof)) return false;
        try
        {
            var expected = Convert.FromBase64String(ComputeProof(key, nonce, senderId, receiverId));
            var actual = Convert.FromBase64String(proof);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static ClipPayload ToPayload(ClipEntry entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind == ClipKind.Image ? KindImage : KindText,
        Text = entry.Text,
        Png = entry.Png,
        Width = entry.Width > 0 ? entry.Width : null,
        Height = entry.Height > 0 ? entry.Height : null,
        Time = ToUnixMs(entry.TimeUtc)
    };

    /// <summary>把收到的内容还原为记录；Id 按内容重新计算，内容无效或超过大小上限时返回 null。</summary>
    public static ClipEntry? FromPayload(ClipPayload? payload, DateTime timeUtc, string originId, string originName)
    {
        if (payload is null) return null;
        switch (payload.Kind)
        {
            case KindText when !string.IsNullOrEmpty(payload.Text) && Encoding.UTF8.GetByteCount(payload.Text) <= MaxClipBytes:
                return ClipEntry.FromText(payload.Text, timeUtc, originId, originName);
            case KindImage when payload.Png is { Length: > 8 and <= MaxClipBytes } png:
                return ClipEntry.FromImage(png, Math.Max(0, payload.Width ?? 0), Math.Max(0, payload.Height ?? 0), timeUtc, originId, originName);
            default:
                return null;
        }
    }

    public static string SanitizeName(string? name, string fallback)
    {
        var s = (name ?? string.Empty).Trim();
        if (s.Length == 0) return fallback;
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return s.Length > 64 ? s[..64] : s;
    }

    public static bool IsValidDeviceId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length is >= 8 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

public sealed class ClipMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("magic")] public string? Magic { get; set; }
    [JsonPropertyName("v")] public int? Version { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("time")] public long? Time { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("proof")] public string? Proof { get; set; }
    [JsonPropertyName("pair_key")] public string? PairKey { get; set; }
    [JsonPropertyName("unpair")] public bool? Unpair { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("inventory")] public List<InventoryItem>? Inventory { get; set; }
    [JsonPropertyName("merge")] public bool? Merge { get; set; }
    [JsonPropertyName("clip")] public ClipPayload? Clip { get; set; }
}

public sealed class InventoryItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("t")] public long Time { get; set; }
}

public sealed class ClipPayload
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = ClipProtocol.KindText;
    [JsonPropertyName("text")] public string? Text { get; set; }
    /// <summary>PNG 字节（JSON 中为 base64）。</summary>
    [JsonPropertyName("png")] public byte[]? Png { get; set; }
    [JsonPropertyName("w")] public int? Width { get; set; }
    [JsonPropertyName("h")] public int? Height { get; set; }
    /// <summary>复制时间（发送方时钟，Unix 毫秒）。</summary>
    [JsonPropertyName("t")] public long Time { get; set; }
}

public sealed class ClipSyncException(string message, string? code = null) : Exception(message)
{
    /// <summary>对方返回的错误码（ClipProtocol.Error*）；连接类错误为 null。</summary>
    public string? Code { get; } = code;
}
