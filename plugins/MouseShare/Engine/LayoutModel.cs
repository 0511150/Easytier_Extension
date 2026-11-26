using System.Text.Json.Serialization;

namespace MouseShare.Engine;

/// <summary>一台显示器在其所属设备桌面中的位置（物理像素）与系统缩放。</summary>
public sealed class MonitorInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
    /// <summary>Windows 缩放（1.5 = 150%）。</summary>
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1;
    [JsonPropertyName("primary")] public bool Primary { get; set; }

    public MonitorInfo Clone() => (MonitorInfo)MemberwiseClone();

    public bool SameGeometry(MonitorInfo o) =>
        Id == o.Id && X == o.X && Y == o.Y && W == o.W && H == o.H && Math.Abs(Scale - o.Scale) < 0.001 && Primary == o.Primary;
}

/// <summary>用户为某台显示器指定的大小依据。</summary>
public sealed class MonitorSetting
{
    public const string ModeAuto = "auto";
    public const string ModeScale = "scale";
    public const string ModeSize = "size";

    /// <summary>auto：跟随系统缩放；scale：指定缩放百分比；size：按对角线英寸换算。</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = ModeAuto;
    [JsonPropertyName("scale")] public double ScalePercent { get; set; } = 100;
    [JsonPropertyName("inches")] public double Inches { get; set; }

    public MonitorSetting Clone() => (MonitorSetting)MemberwiseClone();
}

/// <summary>排列中的一台设备。位置与显示器设置由任一设备修改（rev），显示器几何只由设备自己上报（mrev）。</summary>
public sealed class DeviceEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    /// <summary>主显示器左上角在排列中的位置（布局单位，约 1/96 英寸）。</summary>
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("rev")] public long Rev { get; set; }
    [JsonPropertyName("mrev")] public long MRev { get; set; }
    [JsonPropertyName("monitors")] public List<MonitorInfo> Monitors { get; set; } = [];
    [JsonPropertyName("settings")] public Dictionary<string, MonitorSetting> Settings { get; set; } = new(StringComparer.Ordinal);

    public DeviceEntry Clone() => new()
    {
        Name = Name,
        X = X,
        Y = Y,
        Rev = Rev,
        MRev = MRev,
        Monitors = Monitors.Select(m => m.Clone()).ToList(),
        Settings = Settings.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal)
    };

    public MonitorSetting SettingFor(string monitorId) =>
        Settings.TryGetValue(monitorId, out var s) ? s : new MonitorSetting();
}

/// <summary>所有已配对设备共享的排列文档，按设备逐项“后写者胜”合并。</summary>
public sealed class LayoutDoc
{
    [JsonPropertyName("devices")] public Dictionary<string, DeviceEntry> Devices { get; set; } = new(StringComparer.Ordinal);

    public LayoutDoc Clone() => new()
    {
        Devices = Devices.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal)
    };

    /// <summary>文档中出现过的最大版本号，用于生成严格递增的新版本（不受各机时钟偏差影响）。</summary>
    public long MaxRev() => Devices.Count == 0 ? 0 : Devices.Values.Max(d => Math.Max(d.Rev, d.MRev));

    public long NextRev() => Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), MaxRev() + 1);

    /// <summary>合并对方发来的文档，只接收 <paramref name="known"/> 中的设备。返回是否有变化。</summary>
    public bool Merge(LayoutDoc incoming, IReadOnlySet<string> known)
    {
        bool changed = false;
        foreach (var (id, inc) in incoming.Devices)
        {
            if (!known.Contains(id)) continue;
            if (!Devices.TryGetValue(id, out var cur))
            {
                Devices[id] = inc.Clone();
                changed = true;
                continue;
            }
            if (inc.Rev > cur.Rev)
            {
                cur.X = inc.X;
                cur.Y = inc.Y;
                cur.Rev = inc.Rev;
                cur.Settings = inc.Clone().Settings;
                changed = true;
            }
            if (inc.MRev > cur.MRev)
            {
                cur.Name = inc.Name;
                cur.MRev = inc.MRev;
                cur.Monitors = inc.Monitors.Select(m => m.Clone()).ToList();
                changed = true;
            }
        }
        return changed;
    }
}
