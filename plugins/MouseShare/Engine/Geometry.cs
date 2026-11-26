namespace MouseShare.Engine;

/// <summary>光标移动方向（离开当前显示器的那条边）。</summary>
public enum Edge
{
    Left,
    Right,
    Up,
    Down
}

public readonly record struct PixelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public readonly record struct LayoutRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

/// <summary>一台显示器在排列中的位置：Pixel 为所属设备桌面坐标，Layout 为共享排列坐标，Factor 为每像素对应的布局单位。</summary>
public sealed record PlacedMonitor(string DeviceId, string MonitorId, PixelRect Pixel, LayoutRect Layout, double Factor, bool Primary);

/// <summary>
/// 排列几何。布局单位约为 1/96 英寸：跟随系统缩放时 = 物理像素 / 缩放；指定缩放同理；按尺寸时由对角线英寸换算像素密度。
/// 不同设备之间按布局坐标换算，因此光标跨屏时落点与移动速度都按实际大小对应。
/// </summary>
public static class Geometry
{
    /// <summary>判定两条边相接的容差（布局单位）。</summary>
    public const double EdgeTolerance = 2.0;

    public static double Factor(MonitorInfo m, MonitorSetting? s)
    {
        if (s is not null)
        {
            if (s.Mode == MonitorSetting.ModeScale && s.ScalePercent is >= 50 and <= 500)
            {
                return 100.0 / s.ScalePercent;
            }
            if (s.Mode == MonitorSetting.ModeSize && s.Inches is >= 3 and <= 400 && m.W > 0 && m.H > 0)
            {
                double ppi = Math.Sqrt((double)m.W * m.W + (double)m.H * m.H) / s.Inches;
                return 96.0 / ppi;
            }
        }
        return 1.0 / Math.Clamp(m.Scale, 0.5, 5);
    }

    /// <summary>与“跟随系统缩放”大小相同的对角线英寸数，用作切换到按尺寸时的初始值。</summary>
    public static double DefaultInches(MonitorInfo m) =>
        Math.Round(Math.Sqrt((double)m.W * m.W + (double)m.H * m.H) / (96.0 * Math.Clamp(m.Scale, 0.5, 5)), 1);

    /// <summary>
    /// 计算一台设备各显示器的布局矩形。以主显示器左上角为原点，沿 Windows 中相邻关系逐个排布；
    /// 相邻两屏在像素上顶端/底端/中线对齐时，布局中保持同样的对齐，否则按重叠段中点对应。
    /// </summary>
    public static List<PlacedMonitor> PlaceDevice(string deviceId, DeviceEntry entry)
    {
        var mons = entry.Monitors.Where(m => m.W > 0 && m.H > 0).ToList();
        var result = new List<PlacedMonitor>();
        if (mons.Count == 0) return result;

        var factors = mons.Select(m => Factor(m, entry.SettingFor(m.Id))).ToArray();
        var rects = new LayoutRect?[mons.Count];
        int primary = mons.FindIndex(m => m.Primary);
        if (primary < 0) primary = mons.FindIndex(m => m.X == 0 && m.Y == 0);
        if (primary < 0) primary = 0;

        rects[primary] = new LayoutRect(0, 0, mons[primary].W * factors[primary], mons[primary].H * factors[primary]);
        var queue = new Queue<int>();
        queue.Enqueue(primary);
        while (queue.Count > 0)
        {
            int a = queue.Dequeue();
            var pa = mons[a];
            var la = rects[a]!.Value;
            for (int b = 0; b < mons.Count; b++)
            {
                if (rects[b] is not null) continue;
                var pb = mons[b];
                double w = pb.W * factors[b], h = pb.H * factors[b];
                bool overlapY = Math.Min(pa.Y + pa.H, pb.Y + pb.H) > Math.Max(pa.Y, pb.Y);
                bool overlapX = Math.Min(pa.X + pa.W, pb.X + pb.W) > Math.Max(pa.X, pb.X);

                LayoutRect? placed = null;
                if (overlapY && pb.X == pa.X + pa.W)
                {
                    placed = new LayoutRect(la.Right, AlignAlong(pa.Y, pa.H, pb.Y, pb.H, la.Y, la.H, h, factors[a], factors[b]), w, h);
                }
                else if (overlapY && pb.X + pb.W == pa.X)
                {
                    placed = new LayoutRect(la.X - w, AlignAlong(pa.Y, pa.H, pb.Y, pb.H, la.Y, la.H, h, factors[a], factors[b]), w, h);
                }
                else if (overlapX && pb.Y == pa.Y + pa.H)
                {
                    placed = new LayoutRect(AlignAlong(pa.X, pa.W, pb.X, pb.W, la.X, la.W, w, factors[a], factors[b]), la.Bottom, w, h);
                }
                else if (overlapX && pb.Y + pb.H == pa.Y)
                {
                    placed = new LayoutRect(AlignAlong(pa.X, pa.W, pb.X, pb.W, la.X, la.W, w, factors[a], factors[b]), la.Y - h, w, h);
                }

                if (placed is not null)
                {
                    rects[b] = placed;
                    queue.Enqueue(b);
                }
            }
        }

        // 不相邻的显示器（正常不会出现）按主显示器比例放置
        var p0 = mons[primary];
        for (int i = 0; i < mons.Count; i++)
        {
            rects[i] ??= new LayoutRect((mons[i].X - p0.X) * factors[primary], (mons[i].Y - p0.Y) * factors[primary],
                mons[i].W * factors[i], mons[i].H * factors[i]);
        }

        for (int i = 0; i < mons.Count; i++)
        {
            var r = rects[i]!.Value;
            var m = mons[i];
            result.Add(new PlacedMonitor(deviceId, m.Id, new PixelRect(m.X, m.Y, m.W, m.H),
                new LayoutRect(entry.X + r.X, entry.Y + r.Y, r.W, r.H), factors[i], i == primary));
        }
        return result;
    }

    private static double AlignAlong(int aStart, int aLen, int bStart, int bLen, double laStart, double laLen, double lbLen, double fa, double fb)
    {
        int aEnd = aStart + aLen, bEnd = bStart + bLen;
        if (bStart == aStart) return laStart;
        if (bEnd == aEnd) return laStart + laLen - lbLen;
        if (bStart + bEnd == aStart + aEnd) return laStart + (laLen - lbLen) / 2;
        double c = (Math.Max(aStart, bStart) + Math.Min(aEnd, bEnd)) / 2.0;
        return laStart + (c - aStart) * fa - (c - bStart) * fb;
    }

    public static List<PlacedMonitor> PlaceAll(LayoutDoc doc, IEnumerable<string> deviceIds)
    {
        var all = new List<PlacedMonitor>();
        foreach (var id in deviceIds)
        {
            if (doc.Devices.TryGetValue(id, out var e)) all.AddRange(PlaceDevice(id, e));
        }
        return all;
    }

    /// <summary>把显示器边上的像素位置（左右边为 y，上下边为 x）换算为布局坐标。</summary>
    public static double AlongFromPixel(PlacedMonitor m, Edge edge, double pixel) => edge is Edge.Left or Edge.Right
        ? m.Layout.Y + (pixel + 0.5 - m.Pixel.Y) / m.Pixel.H * m.Layout.H
        : m.Layout.X + (pixel + 0.5 - m.Pixel.X) / m.Pixel.W * m.Layout.W;

    /// <summary>查找从 <paramref name="from"/> 的某条边离开时，紧挨在该位置的其他设备的显示器。</summary>
    public static PlacedMonitor? FindNeighbor(IReadOnlyList<PlacedMonitor> all, PlacedMonitor from, Edge edge, double along)
    {
        PlacedMonitor? best = null;
        double bestGap = EdgeTolerance;
        foreach (var n in all)
        {
            if (n.DeviceId == from.DeviceId) continue;
            double gap;
            bool inRange;
            switch (edge)
            {
                case Edge.Right:
                    gap = Math.Abs(n.Layout.X - from.Layout.Right);
                    inRange = along >= n.Layout.Y && along < n.Layout.Bottom;
                    break;
                case Edge.Left:
                    gap = Math.Abs(n.Layout.Right - from.Layout.X);
                    inRange = along >= n.Layout.Y && along < n.Layout.Bottom;
                    break;
                case Edge.Down:
                    gap = Math.Abs(n.Layout.Y - from.Layout.Bottom);
                    inRange = along >= n.Layout.X && along < n.Layout.Right;
                    break;
                default:
                    gap = Math.Abs(n.Layout.Bottom - from.Layout.Y);
                    inRange = along >= n.Layout.X && along < n.Layout.Right;
                    break;
            }
            if (inRange && gap <= bestGap)
            {
                best = n;
                bestGap = gap;
            }
        }
        return best;
    }

    /// <summary>沿 <paramref name="edge"/> 方向进入 <paramref name="to"/> 时光标的落点（离边缘 1 像素，避免立即弹回）。</summary>
    public static (int X, int Y) EntryPixel(PlacedMonitor to, Edge edge, double along)
    {
        var p = to.Pixel;
        int insetX = Math.Min(1, p.W - 1), insetY = Math.Min(1, p.H - 1);
        if (edge is Edge.Left or Edge.Right)
        {
            double t = (along - to.Layout.Y) / to.Layout.H;
            int y = p.Y + Math.Clamp((int)Math.Floor(t * p.H), 0, p.H - 1);
            return (edge == Edge.Right ? p.X + insetX : p.Right - 1 - insetX, y);
        }
        else
        {
            double t = (along - to.Layout.X) / to.Layout.W;
            int x = p.X + Math.Clamp((int)Math.Floor(t * p.W), 0, p.W - 1);
            return (x, edge == Edge.Down ? p.Y + insetY : p.Bottom - 1 - insetY);
        }
    }

    /// <summary>新加入的设备放在 <paramref name="anchorId"/> 最右侧显示器的右边（顶端对齐）；会重叠时放到所有显示器的最右边。</summary>
    public static (double X, double Y) PlaceToRight(LayoutDoc doc, IEnumerable<string> deviceIds, string newId, string anchorId)
    {
        if (!doc.Devices.TryGetValue(newId, out var entry)) return (0, 0);
        var ids = deviceIds.Where(id => id != newId).ToList();
        var others = PlaceAll(doc, ids);
        var mine = PlaceDevice(newId, entry);
        if (others.Count == 0 || mine.Count == 0) return (entry.X, entry.Y);

        double minX = mine.Min(m => m.Layout.X), minY = mine.Min(m => m.Layout.Y);
        foreach (var pool in new[] { others.Where(m => m.DeviceId == anchorId).ToList(), others })
        {
            if (pool.Count == 0) continue;
            var right = pool.OrderByDescending(m => m.Layout.Right).First();
            double dx = right.Layout.Right - minX, dy = right.Layout.Y - minY;
            if (!Overlaps(mine, others, dx, dy)) return (entry.X + dx, entry.Y + dy);
        }
        var (rx, ry) = Resolve(mine, others);
        return (entry.X + rx, entry.Y + ry);
    }

    /// <summary>
    /// 让一台设备与其他设备相接且不重叠：已满足时返回 (0, 0)，否则返回移动距离最短的平移量。
    /// 用于显示器大小变化后修正位置。
    /// </summary>
    public static (double Dx, double Dy) Resolve(IReadOnlyList<PlacedMonitor> mine, IReadOnlyList<PlacedMonitor> others)
    {
        if (mine.Count == 0 || others.Count == 0) return (0, 0);
        if (!Overlaps(mine, others, 0, 0) && Touching(mine, others, 0, 0)) return (0, 0);

        double bestCost = double.MaxValue;
        (double, double) best = (0, 0);
        foreach (var a in mine)
        {
            var la = a.Layout;
            foreach (var b in others)
            {
                var lb = b.Layout;
                double ty = AlignInto(la.Y, la.H, lb.Y, lb.H);
                double tx = AlignInto(la.X, la.W, lb.X, lb.W);
                foreach (var (cx, cy) in new[]
                         {
                             (lb.Right - la.X, ty),
                             (lb.X - la.Right, ty),
                             (tx, lb.Bottom - la.Y),
                             (tx, lb.Y - la.Bottom)
                         })
                {
                    double cost = Math.Sqrt(cx * cx + cy * cy);
                    if (cost < bestCost && !Overlaps(mine, others, cx, cy))
                    {
                        bestCost = cost;
                        best = (cx, cy);
                    }
                }
            }
        }
        return best;
    }

    private static double AlignInto(double start, double len, double bStart, double bLen)
    {
        double need = Math.Min(len, bLen) * 0.3;
        if (start + len < bStart + need) return bStart + need - (start + len);
        if (start > bStart + bLen - need) return bStart + bLen - need - start;
        return 0;
    }

    public static bool Overlaps(IReadOnlyList<PlacedMonitor> mine, IReadOnlyList<PlacedMonitor> others, double dx, double dy)
    {
        foreach (var a in mine)
        {
            double ax = a.Layout.X + dx, ay = a.Layout.Y + dy;
            foreach (var b in others)
            {
                if (Math.Min(ax + a.Layout.W, b.Layout.Right) - Math.Max(ax, b.Layout.X) > 0.5 &&
                    Math.Min(ay + a.Layout.H, b.Layout.Bottom) - Math.Max(ay, b.Layout.Y) > 0.5) return true;
            }
        }
        return false;
    }

    public static bool Touching(IReadOnlyList<PlacedMonitor> mine, IReadOnlyList<PlacedMonitor> others, double dx, double dy)
    {
        foreach (var a in mine)
        {
            double ax = a.Layout.X + dx, ay = a.Layout.Y + dy, ar = ax + a.Layout.W, ab = ay + a.Layout.H;
            foreach (var b in others)
            {
                var lb = b.Layout;
                bool yOverlap = Math.Min(ab, lb.Bottom) - Math.Max(ay, lb.Y) > 0.5;
                bool xOverlap = Math.Min(ar, lb.Right) - Math.Max(ax, lb.X) > 0.5;
                if (yOverlap && (Math.Abs(ar - lb.X) <= EdgeTolerance || Math.Abs(ax - lb.Right) <= EdgeTolerance)) return true;
                if (xOverlap && (Math.Abs(ab - lb.Y) <= EdgeTolerance || Math.Abs(ay - lb.Bottom) <= EdgeTolerance)) return true;
            }
        }
        return false;
    }
}
