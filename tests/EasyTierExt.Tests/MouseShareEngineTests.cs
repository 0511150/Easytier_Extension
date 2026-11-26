using System.Net;
using System.Security.Cryptography;
using MouseShare.Engine;
using Xunit;

namespace EasyTierExt.Tests;

public class MouseShareGeometryTests
{
    private static DeviceEntry Device(double x, double y, params MonitorInfo[] monitors) =>
        new() { X = x, Y = y, Monitors = [.. monitors] };

    private static MonitorInfo Mon(string id, int x, int y, int w, int h, double scale = 1, bool primary = false) =>
        new() { Id = id, X = x, Y = y, W = w, H = h, Scale = scale, Primary = primary };

    [Fact]
    public void PlaceDevice_MixedDpi_KeepsTopAlignmentAndUsesLogicalSize()
    {
        var entry = Device(100, 50,
            Mon("A", 0, 0, 3840, 2160, 1.5, primary: true),
            Mon("B", 3840, 0, 1920, 1080));

        var placed = Geometry.PlaceDevice("d", entry);
        var a = placed.Single(m => m.MonitorId == "A").Layout;
        var b = placed.Single(m => m.MonitorId == "B").Layout;

        Assert.Equal(new LayoutRect(100, 50, 2560, 1440), a);
        Assert.Equal(new LayoutRect(2660, 50, 1920, 1080), b);
    }

    [Fact]
    public void PlaceDevice_BottomAlignedInPixels_StaysBottomAligned()
    {
        var entry = Device(0, 0,
            Mon("A", 0, 0, 3840, 2160, 1.5, primary: true),
            Mon("B", -1920, 1080, 1920, 1080));

        var b = Geometry.PlaceDevice("d", entry).Single(m => m.MonitorId == "B").Layout;
        Assert.Equal(-1920, b.X, 6);
        Assert.Equal(1440 - 1080, b.Y, 6);
    }

    [Fact]
    public void Factor_FollowsScaleOrPhysicalSize()
    {
        var m = Mon("A", 0, 0, 3840, 2160, 1.5);
        Assert.Equal(1 / 1.5, Geometry.Factor(m, null), 6);
        Assert.Equal(0.5, Geometry.Factor(m, new MonitorSetting { Mode = MonitorSetting.ModeScale, ScalePercent = 200 }), 6);

        // 27 英寸 4K：约 163 PPI，1 布局单位 = 1/96 英寸
        double f = Geometry.Factor(m, new MonitorSetting { Mode = MonitorSetting.ModeSize, Inches = 27 });
        Assert.Equal(96 * 27 / Math.Sqrt(3840.0 * 3840 + 2160.0 * 2160), f, 6);

        // 切换到按尺寸时的初始值与跟随系统大小一致
        double inches = Geometry.DefaultInches(m);
        Assert.Equal(1 / 1.5, Geometry.Factor(m, new MonitorSetting { Mode = MonitorSetting.ModeSize, Inches = inches }), 2);
    }

    [Fact]
    public void FindNeighbor_OnlyThroughSharedEdgeSegment()
    {
        // 设备 A 1920×1440 位于 (0,0)，设备 B 1920×1080 位于 (1920,480)：只有 y ∈ [480, 1440) 的右边缘可以穿越
        var doc = new LayoutDoc();
        doc.Devices["a"] = Device(0, 0, Mon("A", 0, 0, 1920, 1440, primary: true));
        doc.Devices["b"] = Device(1920, 480, Mon("B", 0, 0, 1920, 1080, primary: true));
        var all = Geometry.PlaceAll(doc, ["a", "b"]);
        var a = all.Single(m => m.DeviceId == "a");

        Assert.Null(Geometry.FindNeighbor(all, a, Edge.Right, Geometry.AlongFromPixel(a, Edge.Right, 100)));

        double along = Geometry.AlongFromPixel(a, Edge.Right, 1000);
        var to = Geometry.FindNeighbor(all, a, Edge.Right, along);
        Assert.NotNull(to);
        Assert.Equal("b", to!.DeviceId);
        Assert.Equal((1, 520), Geometry.EntryPixel(to, Edge.Right, along));

        // 反方向：从 B 左边缘回到 A
        double back = Geometry.AlongFromPixel(to, Edge.Left, 520);
        var home = Geometry.FindNeighbor(all, to, Edge.Left, back);
        Assert.Equal("a", home!.DeviceId);
        Assert.Equal((1918, 1000), Geometry.EntryPixel(home, Edge.Left, back));
    }

    [Fact]
    public void EntryPixel_MapsByPhysicalPosition_AcrossDifferentDensities()
    {
        // 左：4K@200%（布局 1920×1080），右：1080p@100%，底端对齐；在左屏 3/4 高度处穿越，右屏也落在 3/4 高度
        var doc = new LayoutDoc();
        doc.Devices["a"] = Device(0, 0, Mon("A", 0, 0, 3840, 2160, 2, primary: true));
        doc.Devices["b"] = Device(1920, 0, Mon("B", 0, 0, 1920, 1080, primary: true));
        var all = Geometry.PlaceAll(doc, ["a", "b"]);
        var a = all.Single(m => m.DeviceId == "a");

        double along = Geometry.AlongFromPixel(a, Edge.Right, 1620);
        var to = Geometry.FindNeighbor(all, a, Edge.Right, along)!;
        var (x, y) = Geometry.EntryPixel(to, Edge.Right, along);
        Assert.Equal(1, x);
        Assert.Equal(810, y);
    }

    [Fact]
    public void Merge_TakesNewerPositionAndMonitorsSeparately_AndIgnoresUnknownDevices()
    {
        var local = new LayoutDoc();
        local.Devices["a"] = new DeviceEntry { Name = "A", X = 0, Rev = 10, MRev = 50, Monitors = [Mon("A1", 0, 0, 100, 100)] };

        var incoming = new LayoutDoc();
        incoming.Devices["a"] = new DeviceEntry { Name = "旧名", X = 500, Rev = 20, MRev = 40, Monitors = [Mon("A1", 0, 0, 1, 1)] };
        incoming.Devices["x"] = new DeviceEntry { Name = "陌生设备", Rev = 99 };

        Assert.True(local.Merge(incoming, new HashSet<string> { "a" }));
        var a = local.Devices["a"];
        Assert.Equal(500, a.X);
        Assert.Equal("A", a.Name);
        Assert.Equal(100, a.Monitors[0].W);
        Assert.False(local.Devices.ContainsKey("x"));
        Assert.True(local.NextRev() > 50);
        Assert.False(local.Merge(incoming, new HashSet<string> { "a" }));
    }

    [Fact]
    public void PlaceToRight_PutsNewDeviceAgainstAnchorWithoutOverlap()
    {
        var doc = new LayoutDoc();
        doc.Devices["self"] = Device(0, 0, Mon("A", 0, 0, 2560, 1440, primary: true), Mon("B", 2560, -480, 1080, 1920));
        doc.Devices["new"] = Device(0, 0, Mon("N", 0, 0, 1920, 1080, primary: true));

        var (x, y) = Geometry.PlaceToRight(doc, ["self", "new"], "new", "self");
        Assert.Equal(3640, x, 6);
        Assert.Equal(-480, y, 6);

        doc.Devices["new"].X = x;
        doc.Devices["new"].Y = y;
        var all = Geometry.PlaceAll(doc, ["self", "new"]);
        var mine = all.Where(m => m.DeviceId == "new").ToList();
        var others = all.Where(m => m.DeviceId != "new").ToList();
        Assert.False(Geometry.Overlaps(mine, others, 0, 0));
        Assert.True(Geometry.Touching(mine, others, 0, 0));
    }

    [Fact]
    public void Resolve_PushesOverlappingDeviceOut_AndAttachesIsolatedDevice()
    {
        var doc = new LayoutDoc();
        doc.Devices["a"] = Device(0, 0, Mon("A", 0, 0, 1920, 1080, primary: true));
        doc.Devices["b"] = Device(1800, 100, Mon("B", 0, 0, 1920, 1080, primary: true));
        var all = Geometry.PlaceAll(doc, ["a", "b"]);
        var (dx, dy) = Geometry.Resolve(all.Where(m => m.DeviceId == "b").ToList(), all.Where(m => m.DeviceId == "a").ToList());
        Assert.Equal(120, dx, 6);
        Assert.Equal(0, dy, 6);

        doc.Devices["b"].X = 5000;
        all = Geometry.PlaceAll(doc, ["a", "b"]);
        (dx, dy) = Geometry.Resolve(all.Where(m => m.DeviceId == "b").ToList(), all.Where(m => m.DeviceId == "a").ToList());
        Assert.Equal(1920 - 5000, dx, 6);
        Assert.Equal(0, dy, 6);
    }
}

public class MouseShareProtocolTests
{
    [Fact]
    public async Task Frames_RoundTrip()
    {
        var ms = new MemoryStream();
        ms.Write(Protocol.KeyFrame(0x41, 0x1E, extended: false, down: true));
        ms.Write(Protocol.PointFrame(MsgType.MouseMove, -1920, 1080));
        ms.Write(Protocol.WheelFrame(-120, horizontal: true));
        ms.Write(Protocol.ButtonFrame(5, down: false));
        ms.Position = 0;

        var (t1, p1) = await Protocol.ReadFrameAsync(ms, CancellationToken.None);
        Assert.Equal(MsgType.Key, t1);
        Assert.Equal(((ushort)0x41, (ushort)0x1E, false, true), Protocol.ReadKey(p1));
        var (t2, p2) = await Protocol.ReadFrameAsync(ms, CancellationToken.None);
        Assert.Equal(MsgType.MouseMove, t2);
        Assert.Equal((-1920, 1080), Protocol.ReadPoint(p2));
        var (_, p3) = await Protocol.ReadFrameAsync(ms, CancellationToken.None);
        Assert.Equal((-120, true), Protocol.ReadWheel(p3));
        var (_, p4) = await Protocol.ReadFrameAsync(ms, CancellationToken.None);
        Assert.Equal((5, false), Protocol.ReadButton(p4));
    }

    [Fact]
    public async Task Hub_PairsWithConsent_ThenAuthenticatesAndDeliversInput()
    {
        var keysA = new Dictionary<string, byte[]>();
        var keysB = new Dictionary<string, byte[]>();
        string idA = "00aa" + Guid.NewGuid().ToString("N")[..28];
        string idB = "ffbb" + Guid.NewGuid().ToString("N")[..28];

        await using var a = new PeerHub(idA, () => "A", id => keysA.GetValueOrDefault(id), port: 0);
        await using var b = new PeerHub(idB, () => "B", id => keysB.GetValueOrDefault(id), port: 0);
        PairRequestInfo? asked = null;
        b.ConsentAsync = (info, _) =>
        {
            asked = info;
            return Task.FromResult(true);
        };
        b.Paired = (id, _, key) => keysB[id] = key;
        b.SetBindAddresses([IPAddress.Loopback]);
        var endpoint = new IPEndPoint(IPAddress.Loopback, b.GetBoundPort(IPAddress.Loopback));

        var key = RandomNumberGenerator.GetBytes(Protocol.KeyBytes);
        var (outcome, name) = await a.RequestPairAsync(idB, endpoint, key, CancellationToken.None);
        Assert.Equal(PairOutcome.Accepted, outcome);
        Assert.Equal("B", name);
        Assert.Equal(idA, asked!.DeviceId);
        Assert.Equal(key, keysB[idA]);
        keysA[idB] = key;

        var received = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.MessageReceived += (s, type, payload) =>
        {
            if (s.PeerId == idA && type == MsgType.MouseMove) received.TrySetResult(Protocol.ReadPoint(payload));
        };
        a.SessionOpened += _ => opened.TrySetResult();

        a.Connect(idB, endpoint);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(a.Send(idB, Protocol.PointFrame(MsgType.MouseMove, 12, 34)));
        Assert.Equal((12, 34), await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Hub_RejectsWrongKey_AndReportsUnpaired()
    {
        string idA = Guid.NewGuid().ToString("N");
        string idB = Guid.NewGuid().ToString("N");
        var keyB = RandomNumberGenerator.GetBytes(Protocol.KeyBytes);
        var keyForB = RandomNumberGenerator.GetBytes(Protocol.KeyBytes); // A 持有的密钥与 B 的不同
        bool knowsA = true;

        await using var b = new PeerHub(idB, () => "B", id => id == idA && knowsA ? keyB : null, port: 0);
        b.SetBindAddresses([IPAddress.Loopback]);
        var endpoint = new IPEndPoint(IPAddress.Loopback, b.GetBoundPort(IPAddress.Loopback));
        bool bOpened = false;
        b.SessionOpened += _ => bOpened = true;

        var unpaired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var a = new PeerHub(idA, () => "A", id => id == idB ? keyForB : null, port: 0)
        {
            RemoteUnpaired = id => unpaired.TrySetResult(id)
        };

        a.Connect(idB, endpoint);
        await Task.Delay(1500);
        Assert.False(a.IsConnected(idB));
        Assert.False(bOpened);

        knowsA = false;
        a.Connect(idB, endpoint);
        Assert.Equal(idB, await unpaired.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
