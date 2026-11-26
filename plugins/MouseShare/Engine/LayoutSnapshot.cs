namespace MouseShare.Engine;

/// <summary>输入引擎使用的不可变排列快照：只包含本机与当前已连接的设备。</summary>
public sealed class LayoutSnapshot
{
    private readonly Dictionary<string, List<PlacedMonitor>> _byDevice;

    public LayoutSnapshot(string localId, IEnumerable<PlacedMonitor> monitors)
    {
        LocalId = localId;
        All = monitors.ToList();
        _byDevice = All.GroupBy(m => m.DeviceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        Local = Of(localId);
    }

    public string LocalId { get; }
    public IReadOnlyList<PlacedMonitor> All { get; }
    public IReadOnlyList<PlacedMonitor> Local { get; }
    public bool HasRemote => _byDevice.Keys.Any(k => k != LocalId);

    public IReadOnlyList<PlacedMonitor> Of(string deviceId) =>
        _byDevice.TryGetValue(deviceId, out var list) ? list : [];

    public bool LocalContains(int x, int y)
    {
        foreach (var m in Local)
        {
            if (m.Pixel.Contains(x, y)) return true;
        }
        return false;
    }

    /// <summary>本机包含该点的显示器；点在桌面外时返回最近的显示器。</summary>
    public PlacedMonitor? LocalAtOrNearest(int x, int y)
    {
        PlacedMonitor? best = null;
        double bestDist = double.MaxValue;
        foreach (var m in Local)
        {
            if (m.Pixel.Contains(x, y)) return m;
            double dx = Math.Max(Math.Max(m.Pixel.X - x, 0), x - (m.Pixel.Right - 1));
            double dy = Math.Max(Math.Max(m.Pixel.Y - y, 0), y - (m.Pixel.Bottom - 1));
            double d = dx * dx + dy * dy;
            if (d < bestDist)
            {
                bestDist = d;
                best = m;
            }
        }
        return best;
    }

    public PlacedMonitor? Find(string deviceId, string monitorId) =>
        Of(deviceId).FirstOrDefault(m => m.MonitorId == monitorId);
}
