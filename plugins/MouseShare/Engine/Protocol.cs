using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseShare.Engine;

public enum MsgType : byte
{
    // 握手
    Challenge = 1,
    Auth = 2,
    AuthOk = 3,
    AuthFail = 4,
    PairRequest = 5,
    PairResult = 6,

    // 连接维护
    Ping = 10,
    Pong = 11,

    // 排列与管理
    Layout = 20,
    Unpair = 21,
    Identify = 22,

    // 键鼠（控制方 -> 被控方，TakeBack 反向）
    Enter = 30,
    Leave = 31,
    MouseMove = 32,
    MouseButton = 33,
    MouseWheel = 34,
    Key = 35,
    TakeBack = 36
}

/// <summary>
/// 帧格式：[u32 LE 长度（含类型字节）][u8 类型][载荷]。握手与管理消息为 UTF-8 JSON，键鼠消息为紧凑二进制。
/// 连接先由被连方发 Challenge，连接方回 Auth（或 PairRequest），双方用配对密钥做 HMAC 互相验证后才收发键鼠数据。
/// </summary>
public static class Protocol
{
    public const int Version = 1;
    public const int DefaultPort = 39720;
    public const int MaxFrame = 4 * 1024 * 1024;
    public const int HandshakeTimeoutMs = 5_000;
    public const int ConnectTimeoutMs = 4_000;
    public const int PingIntervalMs = 1_000;
    public const int DeadAfterMs = 4_500;
    public const int ConsentTimeoutMs = 30_000;
    public const int KeyBytes = 32;

    public static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static byte[] Frame(MsgType type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[5 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)(payload.Length + 1));
        buf[4] = (byte)type;
        payload.CopyTo(buf.AsSpan(5));
        return buf;
    }

    public static byte[] Frame(MsgType type) => Frame(type, ReadOnlySpan<byte>.Empty);

    public static byte[] JsonFrame<T>(MsgType type, T body) => Frame(type, JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions));

    public static T? ParseJson<T>(byte[] payload) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(payload, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public static MsgType TypeOf(byte[] frame) => (MsgType)frame[4];

    public static async Task<(MsgType Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len < 1 || len > MaxFrame) throw new InvalidDataException("帧长度无效");
        var payload = new byte[len - 1];
        if (payload.Length > 0) await stream.ReadExactlyAsync(payload, ct);
        return ((MsgType)header[4], payload);
    }

    // ---------------- 键鼠二进制载荷 ----------------

    public static byte[] PointFrame(MsgType type, int x, int y)
    {
        Span<byte> p = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(p, x);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], y);
        return Frame(type, p);
    }

    public static (int X, int Y) ReadPoint(byte[] p) =>
        p.Length >= 8 ? (BinaryPrimitives.ReadInt32LittleEndian(p), BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(4))) : (0, 0);

    /// <summary>button：1 左键、2 右键、3 中键、4 侧键 1、5 侧键 2。</summary>
    public static byte[] ButtonFrame(int button, bool down) => Frame(MsgType.MouseButton, [(byte)button, (byte)(down ? 1 : 0)]);

    public static (int Button, bool Down) ReadButton(byte[] p) => p.Length >= 2 ? (p[0], p[1] != 0) : (0, false);

    public static byte[] WheelFrame(int delta, bool horizontal)
    {
        Span<byte> p = stackalloc byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(p, delta);
        p[4] = (byte)(horizontal ? 1 : 0);
        return Frame(MsgType.MouseWheel, p);
    }

    public static (int Delta, bool Horizontal) ReadWheel(byte[] p) =>
        p.Length >= 5 ? (BinaryPrimitives.ReadInt32LittleEndian(p), p[4] != 0) : (0, false);

    public static byte[] KeyFrame(ushort vk, ushort scan, bool extended, bool down)
    {
        Span<byte> p = stackalloc byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(p, vk);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], scan);
        p[4] = (byte)((extended ? 1 : 0) | (down ? 2 : 0));
        return Frame(MsgType.Key, p);
    }

    public static (ushort Vk, ushort Scan, bool Extended, bool Down) ReadKey(byte[] p) =>
        p.Length >= 5
            ? (BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)), (p[4] & 1) != 0, (p[4] & 2) != 0)
            : ((ushort)0, (ushort)0, false, false);

    // ---------------- 认证 ----------------

    public static byte[] Nonce() => RandomNumberGenerator.GetBytes(16);

    /// <summary>设备 id：1~64 个字母、数字或连字符（本插件生成的是 32 位十六进制）。</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>HMAC-SHA256(key, role | nonceA | nonceB | idA | idB)，role 区分双方方向，避免反射攻击。</summary>
    public static byte[] Mac(byte[] key, string role, byte[] nonceA, byte[] nonceB, string idA, string idB)
    {
        using var h = new HMACSHA256(key);
        var ms = new MemoryStream();
        void Put(byte[] b)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, b.Length);
            ms.Write(len);
            ms.Write(b);
        }
        Put(Encoding.UTF8.GetBytes(role));
        Put(nonceA);
        Put(nonceB);
        Put(Encoding.UTF8.GetBytes(idA));
        Put(Encoding.UTF8.GetBytes(idB));
        return h.ComputeHash(ms.ToArray());
    }

    public static bool MacEquals(byte[] expected, string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class ChallengeMsg
{
    [JsonPropertyName("v")] public int Version { get; set; } = Protocol.Version;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
}

public sealed class AuthMsg
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("mac")] public string Mac { get; set; } = string.Empty;
}

public sealed class AuthOkMsg
{
    [JsonPropertyName("mac")] public string Mac { get; set; } = string.Empty;
}

public sealed class AuthFailMsg
{
    public const string Unpaired = "unpaired";
    public const string BadMac = "bad_mac";

    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

public sealed class PairRequestMsg
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
}

public sealed class PairResultMsg
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public sealed class IdentifyMsg
{
    /// <summary>显示器 id -> 编号。</summary>
    [JsonPropertyName("numbers")] public Dictionary<string, string> Numbers { get; set; } = new(StringComparer.Ordinal);
}
