using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipboardShare.Engine;

/// <summary>与一台设备的配对关系。配对是双向的：对方同意后双方都持有同一把密钥，各自决定是否向对方发送。</summary>
public sealed record PairingRecord
{
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    /// <summary>base64 共享密钥，只用于计算 HMAC 证明，不在网络上重复传输。</summary>
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    /// <summary>是否把本机复制的内容发送给该设备。</summary>
    [JsonPropertyName("send_enabled")] public bool SendEnabled { get; init; } = true;
    /// <summary>最近一次开启发送的时间：更早复制的内容不会在重连合并时补发给对方。</summary>
    [JsonPropertyName("send_enabled_since")] public DateTime SendEnabledSinceUtc { get; init; }
    /// <summary>对方已取消共享（连接时被拒绝），需要重新发起共享。</summary>
    [JsonPropertyName("revoked")] public bool Revoked { get; init; }
    [JsonPropertyName("paired_at")] public DateTime PairedAtUtc { get; init; }

    [JsonIgnore]
    public byte[] KeyBytes => Convert.FromBase64String(Key);
}

/// <summary>配对记录，保存在插件数据目录的 pairs.json（filePath 为 null 时只在内存中，供测试使用）。</summary>
public sealed class PairingStore
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string? _filePath;
    private readonly Dictionary<string, PairingRecord> _records = new(StringComparer.Ordinal);

    public PairingStore(string? filePath)
    {
        _filePath = filePath;
        if (filePath is null || !File.Exists(filePath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<PairingRecord>>(File.ReadAllText(filePath)) ?? [];
            foreach (var r in list.Where(r => ClipProtocol.IsValidDeviceId(r.DeviceId) && IsValidKey(r.Key)))
            {
                _records[r.DeviceId] = r;
            }
        }
        catch
        {
            // 文件损坏时当作没有配对，需要重新共享
        }
    }

    public PairingRecord? Get(string deviceId)
    {
        lock (_lock) return _records.GetValueOrDefault(deviceId);
    }

    public List<PairingRecord> All()
    {
        lock (_lock) return _records.Values.ToList();
    }

    public void Upsert(PairingRecord record)
    {
        lock (_lock)
        {
            _records[record.DeviceId] = record;
            Save();
        }
    }

    /// <summary>修改已有记录；记录不存在时返回 false。</summary>
    public bool Update(string deviceId, Func<PairingRecord, PairingRecord> change)
    {
        lock (_lock)
        {
            if (!_records.TryGetValue(deviceId, out var r)) return false;
            _records[deviceId] = change(r);
            Save();
            return true;
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_lock)
        {
            if (!_records.Remove(deviceId)) return false;
            Save();
            return true;
        }
    }

    private void Save()
    {
        if (_filePath is null) return;
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_records.Values.ToList(), FileJsonOptions));
        }
        catch
        {
        }
    }

    private static bool IsValidKey(string key)
    {
        try
        {
            return Convert.FromBase64String(key).Length == ClipProtocol.KeyBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
