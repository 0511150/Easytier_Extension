using System.Security.Cryptography;
using System.Text;

namespace ClipboardShare.Engine;

public enum ClipKind
{
    Text,
    Image
}

/// <summary>
/// 一条剪贴板记录。Id 由内容哈希得出：同样的内容在任何设备上 Id 都相同，合并时按 Id 去重并保留最新的复制时间。
/// </summary>
public sealed record ClipEntry(
    string Id,
    ClipKind Kind,
    string? Text,
    byte[]? Png,
    int Width,
    int Height,
    DateTime TimeUtc,
    string OriginDeviceId,
    string OriginName)
{
    /// <summary>掉线重连后通过合并收到。</summary>
    public bool Merged { get; init; }

    /// <summary>复制时同步处于关闭状态（或图片同步关闭），只保留在本机，任何时候都不会发送。</summary>
    public bool LocalOnly { get; init; }

    public long SizeBytes => Kind == ClipKind.Text ? Encoding.UTF8.GetByteCount(Text ?? string.Empty) : Png?.LongLength ?? 0;

    public static ClipEntry FromText(string text, DateTime timeUtc, string originId, string originName) =>
        new(ComputeId(ClipKind.Text, Encoding.UTF8.GetBytes(text)), ClipKind.Text, text, null, 0, 0, timeUtc, originId, originName);

    public static ClipEntry FromImage(byte[] png, int width, int height, DateTime timeUtc, string originId, string originName) =>
        new(ComputeId(ClipKind.Image, png), ClipKind.Image, null, png, width, height, timeUtc, originId, originName);

    public static string ComputeId(ClipKind kind, ReadOnlySpan<byte> content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData([(byte)kind]);
        sha.AppendData(content);
        return Convert.ToHexStringLower(sha.GetHashAndReset().AsSpan(0, 16));
    }
}

public enum ApplyOutcome
{
    Added,
    Updated,
    Ignored
}

/// <summary>
/// 只保存在内存中的剪贴板历史，不限条数，按复制时间定时清除。
/// 合并规则：同一 Id 保留复制时间最新的一条；删除过的内容只有再次被复制（时间更新）才会重新出现。
/// </summary>
public sealed class ClipHistory
{
    /// <summary>对方的同一条记录时间差在此范围内视为已拥有，避免两台设备时钟误差导致重复发送。</summary>
    private static readonly TimeSpan SameTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, ClipEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _tombstones = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;
    private TimeSpan? _ttl;

    /// <param name="ttl">复制后多久清除；null 表示不自动清除。</param>
    public ClipHistory(TimeSpan? ttl, Func<DateTime>? utcClock = null)
    {
        _ttl = ttl;
        _clock = utcClock ?? (() => DateTime.UtcNow);
    }

    /// <summary>记录新增、更新、删除或清除时触发。</summary>
    public event Action? Changed;

    public TimeSpan? Ttl
    {
        get
        {
            lock (_lock) return _ttl;
        }
        set
        {
            lock (_lock) _ttl = value;
            Expire();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    /// <summary>本机复制：新增记录，或把已有的同内容记录更新为本机最新复制。</summary>
    public void RecordLocal(ClipEntry entry)
    {
        lock (_lock)
        {
            _tombstones.Remove(entry.Id);
            _entries[entry.Id] = entry;
        }
        Changed?.Invoke();
    }

    /// <summary>收到其他设备的记录。过期、已删除或不比本地新的记录会被忽略。</summary>
    public ApplyOutcome ApplyRemote(ClipEntry entry)
    {
        ApplyOutcome outcome;
        lock (_lock)
        {
            if (IsExpired(entry.TimeUtc, _clock())) return ApplyOutcome.Ignored;
            if (_tombstones.TryGetValue(entry.Id, out var deleted) && entry.TimeUtc <= deleted) return ApplyOutcome.Ignored;
            if (_entries.TryGetValue(entry.Id, out var existing) && existing.TimeUtc >= entry.TimeUtc) return ApplyOutcome.Ignored;

            outcome = existing is null ? ApplyOutcome.Added : ApplyOutcome.Updated;
            _tombstones.Remove(entry.Id);
            _entries[entry.Id] = entry with { LocalOnly = false };
        }
        Changed?.Invoke();
        return outcome;
    }

    public ClipEntry? Get(string id)
    {
        lock (_lock) return _entries.GetValueOrDefault(id);
    }

    /// <summary>按复制时间从新到旧。</summary>
    public List<ClipEntry> Snapshot()
    {
        lock (_lock) return _entries.Values.OrderByDescending(e => e.TimeUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>全部记录的 Id 与复制时间，握手时发给对方用于计算差集。</summary>
    public Dictionary<string, DateTime> Inventory()
    {
        lock (_lock) return _entries.Values.ToDictionary(e => e.Id, e => e.TimeUtc, StringComparer.Ordinal);
    }

    /// <summary>
    /// 重连合并时需要发给对方的记录：只发本机复制的（不转发从别的设备收到的内容），
    /// 不发暂停期间的内容（LocalOnly 或早于对该设备开启发送的时间），且对方没有或对方的更旧。按时间从旧到新。
    /// </summary>
    public List<ClipEntry> SelectForPeer(string selfDeviceId, DateTime sendEnabledSinceUtc, IReadOnlyDictionary<string, DateTime> remoteHas)
    {
        lock (_lock)
        {
            return _entries.Values
                .Where(e => e.OriginDeviceId == selfDeviceId && !e.LocalOnly && e.TimeUtc >= sendEnabledSinceUtc)
                .Where(e => !remoteHas.TryGetValue(e.Id, out var t) || t < e.TimeUtc - SameTimeTolerance)
                .OrderBy(e => e.TimeUtc)
                .ToList();
        }
    }

    /// <summary>删除一条记录（只影响本机）；之后合并收到同一次复制的记录也不会再出现。</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (!_entries.Remove(id, out var entry)) return false;
            _tombstones[id] = entry.TimeUtc;
        }
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return;
            foreach (var e in _entries.Values) _tombstones[e.Id] = e.TimeUtc;
            _entries.Clear();
        }
        Changed?.Invoke();
    }

    /// <summary>清除超过保留时长的记录，返回清除的条数。</summary>
    public int Expire()
    {
        int removed = 0;
        lock (_lock)
        {
            var now = _clock();
            foreach (var e in _entries.Values.Where(e => IsExpired(e.TimeUtc, now)).ToList())
            {
                _entries.Remove(e.Id);
                removed++;
            }
            foreach (var (id, t) in _tombstones.Where(kv => IsExpired(kv.Value, now)).ToList())
            {
                _tombstones.Remove(id);
            }
        }
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    private bool IsExpired(DateTime timeUtc, DateTime nowUtc) => _ttl is { } ttl && nowUtc - timeUtc > ttl;
}
