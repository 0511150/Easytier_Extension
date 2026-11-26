using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using ClipboardShare.Clipboard;
using ClipboardShare.Engine;
using Xunit;

namespace EasyTierExt.Tests;

public class ClipboardShareHistoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameContent_HasSameId_AndNewerCopyWins()
    {
        var now = T0.AddMinutes(10);
        var history = new ClipHistory(TimeSpan.FromHours(1), () => now);
        var a = ClipEntry.FromText("hello", T0, "dev-aaaaaaaa", "A");
        var b = ClipEntry.FromText("hello", T0.AddMinutes(1), "dev-bbbbbbbb", "B");
        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Id, ClipEntry.FromText("hello!", T0, "dev-aaaaaaaa", "A").Id);

        history.RecordLocal(a);
        Assert.Equal(ApplyOutcome.Updated, history.ApplyRemote(b));
        Assert.Equal(ApplyOutcome.Ignored, history.ApplyRemote(a));
        var only = Assert.Single(history.Snapshot());
        Assert.Equal("dev-bbbbbbbb", only.OriginDeviceId);
        Assert.Equal(T0.AddMinutes(1), only.TimeUtc);
    }

    [Fact]
    public void DeletedEntry_IsNotResurrectedByMerge_UnlessCopiedAgain()
    {
        var now = T0.AddMinutes(10);
        var history = new ClipHistory(TimeSpan.FromHours(1), () => now);
        var e = ClipEntry.FromText("secret", T0, "dev-aaaaaaaa", "A");
        history.RecordLocal(e);
        Assert.True(history.Delete(e.Id));

        Assert.Equal(ApplyOutcome.Ignored, history.ApplyRemote(e with { OriginDeviceId = "dev-bbbbbbbb" }));
        Assert.Equal(0, history.Count);
        Assert.Equal(ApplyOutcome.Added, history.ApplyRemote(e with { TimeUtc = T0.AddMinutes(5) }));

        history.Clear();
        Assert.Equal(ApplyOutcome.Ignored, history.ApplyRemote(e with { TimeUtc = T0.AddMinutes(5) }));
    }

    [Fact]
    public void Expire_RemovesByCopyTime_WithoutCountLimit()
    {
        var now = T0;
        var history = new ClipHistory(TimeSpan.FromHours(1), () => now);
        for (int i = 0; i < 5000; i++)
        {
            history.RecordLocal(ClipEntry.FromText($"item {i}", T0.AddSeconds(-i), "dev-aaaaaaaa", "A"));
        }
        Assert.Equal(5000, history.Count);

        // 30 分钟后：复制已超过 1 小时的（早于 T0 1800 秒以上）被清除，剩下第 0 ~ 1800 秒的 1801 条
        now = T0.AddMinutes(30);
        int removed = history.Expire();
        Assert.Equal(5000 - 1801, removed);
        Assert.Equal(1801, history.Count);

        // 过期的远端记录直接忽略；不清除时永远保留
        Assert.Equal(ApplyOutcome.Ignored, history.ApplyRemote(ClipEntry.FromText("old", T0.AddHours(-2), "dev-bbbbbbbb", "B")));
        history.Ttl = null;
        Assert.Equal(ApplyOutcome.Added, history.ApplyRemote(ClipEntry.FromText("old", T0.AddHours(-2), "dev-bbbbbbbb", "B")));
    }

    [Fact]
    public void SelectForPeer_SendsOnlyOwnEligibleEntriesThePeerLacks()
    {
        var now = T0.AddMinutes(30);
        var history = new ClipHistory(null, () => now);
        const string self = "dev-aaaaaaaa";
        var beforeEnable = ClipEntry.FromText("before", T0, self, "A");
        var own = ClipEntry.FromText("own", T0.AddMinutes(5), self, "A");
        var ownKnown = ClipEntry.FromText("known", T0.AddMinutes(6), self, "A");
        var ownNewer = ClipEntry.FromText("newer", T0.AddMinutes(7), self, "A");
        var localOnly = ClipEntry.FromText("paused", T0.AddMinutes(8), self, "A") with { LocalOnly = true };
        var received = ClipEntry.FromText("from b", T0.AddMinutes(9), "dev-bbbbbbbb", "B");
        foreach (var e in new[] { beforeEnable, own, ownKnown, ownNewer, localOnly }) history.RecordLocal(e);
        history.ApplyRemote(received);

        var remoteHas = new Dictionary<string, DateTime>
        {
            [ownKnown.Id] = ownKnown.TimeUtc.AddMilliseconds(300), // 时钟误差内视为已有
            [ownNewer.Id] = ownNewer.TimeUtc.AddMinutes(-3)
        };
        var toSend = history.SelectForPeer(self, T0.AddMinutes(1), remoteHas);
        Assert.Equal([own.Id, ownNewer.Id], toSend.Select(e => e.Id));
    }
}

/// <summary>两个同步节点在回环地址上互相连接，验证配对、实时同步与重连合并。</summary>
[Collection(nameof(ClipboardShareNodeTests))]
public class ClipboardShareNodeTests
{
    private sealed class TestNode : IAsyncDisposable
    {
        public TestNode(string name)
        {
            Name = name;
            Id = Guid.NewGuid().ToString("N");
            History = new ClipHistory(TimeSpan.FromHours(1));
            Pairs = new PairingStore(null);
            Node = new ClipSyncNode(Id, () => Name, Pairs, History, (r, ct) => Consent(r, ct), port: 0);
            Node.ClipsReceived += Received.Enqueue;
            Node.SetBindAddresses([IPAddress.Loopback]);
            Port = Node.GetBoundPort(IPAddress.Loopback);
            Assert.True(Port > 0, Node.LastBindError);
        }

        public string Name { get; }
        public string Id { get; }
        public ClipHistory History { get; }
        public PairingStore Pairs { get; }
        public ClipSyncNode Node { get; }
        public int Port { get; }
        public ConcurrentQueue<ClipsReceivedEventArgs> Received { get; } = new();
        public ConcurrentQueue<PairRequestInfo> Asked { get; } = new();
        public bool Accept { get; set; } = true;

        public PeerEndpoint Endpoint => new(Id, Name, IPAddress.Loopback, Port, null, "test-net");

        private Task<bool> Consent(PairRequestInfo request, CancellationToken ct)
        {
            Asked.Enqueue(request);
            return Task.FromResult(Accept);
        }

        public ClipEntry Copy(string text)
        {
            var e = ClipEntry.FromText(text, DateTime.UtcNow, Id, Name);
            History.RecordLocal(e);
            Node.Publish(e);
            return e;
        }

        public PeerStatus? Peer(TestNode other) => Node.GetPeers().FirstOrDefault(p => p.DeviceId == other.Id);

        public ValueTask DisposeAsync() => Node.DisposeAsync();
    }

    private static void SeeEachOther(TestNode a, TestNode b)
    {
        a.Node.UpdateEndpoints([b.Endpoint]);
        b.Node.UpdateEndpoints([a.Endpoint]);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"等待超时：{what}");
            await Task.Delay(20);
        }
    }

    private static async Task PairAsync(TestNode a, TestNode b)
    {
        Assert.Equal(PairResult.Accepted, await a.Node.RequestPairAsync(b.Id));
        await WaitUntil(() => a.Peer(b) is { Link: LinkState.Connected, Receiving: true } && b.Peer(a) is { Link: LinkState.Connected, Receiving: true },
            "双向连接建立");
    }

    [Fact]
    public async Task FirstShare_RequiresConsent_ThenBothSidesArePaired()
    {
        await using var a = new TestNode("PC-A");
        await using var b = new TestNode("PC-B");
        SeeEachOther(a, b);

        b.Accept = false;
        Assert.Equal(PairResult.Rejected, await a.Node.RequestPairAsync(b.Id));
        Assert.Null(a.Pairs.Get(b.Id));
        Assert.Null(b.Pairs.Get(a.Id));
        var asked = Assert.Single(b.Asked);
        Assert.Equal("PC-A", asked.Name);
        Assert.Equal(a.Id, asked.DeviceId);

        b.Accept = true;
        await PairAsync(a, b);
        Assert.Equal(a.Pairs.Get(b.Id)!.Key, b.Pairs.Get(a.Id)!.Key);
        Assert.Equal("PC-B", a.Pairs.Get(b.Id)!.Name);
        // 只在第一次询问；之后的连接（包括 B 连回 A）都不再询问
        Assert.Equal(2, b.Asked.Count);
        Assert.Empty(a.Asked);
    }

    [Fact]
    public async Task Copy_IsDeliveredImmediately_InBothDirections()
    {
        await using var a = new TestNode("PC-A");
        await using var b = new TestNode("PC-B");
        SeeEachOther(a, b);
        await PairAsync(a, b);

        var x = a.Copy("来自 A 的文字");
        await WaitUntil(() => b.History.Get(x.Id) is not null, "B 收到 A 的复制");
        var got = b.History.Get(x.Id)!;
        Assert.Equal(a.Id, got.OriginDeviceId);
        Assert.False(got.Merged);
        Assert.Contains(b.Received, r => !r.Merge && r.Entries.Any(e => e.Id == x.Id));

        var y = b.Copy("来自 B 的文字");
        await WaitUntil(() => a.History.Get(y.Id) is not null, "A 收到 B 的复制");

        // 收到的内容不会被转发回去，A 的历史里只有两条
        await Task.Delay(300);
        Assert.Equal(2, a.History.Count);
        Assert.Equal(2, b.History.Count);
    }

    [Fact]
    public async Task Reconnect_MergesWhatEachSideCopiedWhileOffline()
    {
        await using var a = new TestNode("PC-A");
        await using var b = new TestNode("PC-B");
        SeeEachOther(a, b);
        await PairAsync(a, b);

        // 掉线：双方互相看不到
        a.Node.UpdateEndpoints([]);
        b.Node.UpdateEndpoints([]);
        await WaitUntil(() => a.Peer(b) is { Receiving: false, Link: LinkState.Idle } && b.Peer(a) is { Receiving: false, Link: LinkState.Idle }, "连接断开");

        var x1 = a.Copy("A 离线时复制 1");
        await Task.Delay(20);
        var y1 = b.Copy("B 离线时复制 1");
        await Task.Delay(20);
        var x2 = a.Copy("A 离线时复制 2");
        Assert.Null(b.History.Get(x1.Id));

        SeeEachOther(a, b);
        await WaitUntil(() => b.History.Get(x1.Id) is not null && b.History.Get(x2.Id) is not null && a.History.Get(y1.Id) is not null, "重连合并");

        Assert.True(b.History.Get(x1.Id)!.Merged);
        Assert.Equal(x1.TimeUtc, b.History.Get(x1.Id)!.TimeUtc, TimeSpan.FromSeconds(1));
        var batch = Assert.Single(b.Received, r => r.Merge);
        Assert.Equal(2, batch.Entries.Count);
        // 两边合并后的历史按复制时间排序一致
        Assert.Equal(a.History.Snapshot().Select(e => e.Id), b.History.Snapshot().Select(e => e.Id));
    }

    [Fact]
    public async Task PausedSending_DoesNotBackfill_AndContentBeforePairingIsNotShared()
    {
        await using var a = new TestNode("PC-A");
        await using var b = new TestNode("PC-B");
        var beforePairing = a.Copy("配对前复制的内容");
        await Task.Delay(20);
        SeeEachOther(a, b);
        await PairAsync(a, b);

        a.Node.SetSendEnabled(b.Id, false);
        await WaitUntil(() => b.Peer(a) is { Receiving: false }, "A 停止发送");
        var paused = a.Copy("暂停期间复制的内容");
        await Task.Delay(20);
        a.Node.SetSendEnabled(b.Id, true);
        await WaitUntil(() => b.Peer(a) is { Receiving: true }, "A 恢复发送");
        var after = a.Copy("恢复后复制的内容");

        await WaitUntil(() => b.History.Get(after.Id) is not null, "恢复后的内容送达");
        Assert.Null(b.History.Get(paused.Id));
        Assert.Null(b.History.Get(beforePairing.Id));
    }

    [Fact]
    public async Task Unpair_RemovesPairingOnBothSides_AndForgedKeyIsRejected()
    {
        await using var a = new TestNode("PC-A");
        await using var b = new TestNode("PC-B");
        await using var c = new TestNode("PC-C");
        SeeEachOther(a, b);
        await PairAsync(a, b);
        int askedBefore = b.Asked.Count;

        a.Node.Unpair(b.Id);
        await WaitUntil(() => b.Pairs.Get(a.Id) is null, "B 收到取消共享");
        Assert.Null(a.Pairs.Get(b.Id));
        await WaitUntil(() => b.Peer(a) is { Link: LinkState.Idle, Receiving: false }, "B 停止向 A 发送");

        // C 伪造一条与 B 的配对：B 不认这把密钥，C 被告知“对方已取消共享”，内容不会写入 B 的历史
        c.Pairs.Upsert(new PairingRecord
        {
            DeviceId = b.Id,
            Name = "PC-B",
            Key = Convert.ToBase64String(ClipProtocol.NewKey()),
            SendEnabled = true,
            SendEnabledSinceUtc = DateTime.UtcNow.AddMinutes(-1)
        });
        var forged = c.Copy("伪造的内容");
        c.Node.UpdateEndpoints([b.Endpoint]);
        await WaitUntil(() => c.Pairs.Get(b.Id) is { Revoked: true }, "C 被拒绝");
        Assert.Null(b.History.Get(forged.Id));
        Assert.Equal(askedBefore, b.Asked.Count);
    }

    [Fact]
    public async Task SyncDisabled_RejectsIncoming()
    {
        await using var a = new TestNode("PC-A");
        await using var b = new TestNode("PC-B");
        SeeEachOther(a, b);
        await PairAsync(a, b);

        b.Node.SetSyncEnabled(false);
        await WaitUntil(() => a.Peer(b) is { Receiving: false } && b.Peer(a) is { Receiving: false }, "B 关闭同步");
        var x = a.Copy("B 关闭同步时复制");
        await Task.Delay(500);
        Assert.Null(b.History.Get(x.Id));
        await WaitUntil(() => a.Peer(b)?.Error == ClipSyncNode.ErrorText(ClipProtocol.ErrorPaused), "A 显示对方已暂停");
    }
}

public class ClipboardShareProtocolTests
{
    [Fact]
    public async Task Frame_RoundTrips_ImageBytes_AndProofIsBoundToBothDevices()
    {
        var png = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var entry = ClipEntry.FromImage(png, 40, 30, DateTime.UtcNow, "dev-aaaaaaaa", "A");
        using var ms = new MemoryStream();
        await ClipProtocol.WriteAsync(ms, new ClipMessage { Type = ClipProtocol.TypeClip, Merge = true, Clip = ClipProtocol.ToPayload(entry) }, CancellationToken.None);
        ms.Position = 0;
        var msg = await ClipProtocol.ReadAsync(ms, CancellationToken.None);
        Assert.NotNull(msg);
        Assert.True(msg!.Merge);
        var back = ClipProtocol.FromPayload(msg.Clip, entry.TimeUtc, "dev-aaaaaaaa", "A");
        Assert.Equal(entry.Id, back!.Id);
        Assert.Equal(png, back.Png);
        Assert.Null(await ClipProtocol.ReadAsync(ms, CancellationToken.None));

        var key = ClipProtocol.NewKey();
        var proof = ClipProtocol.ComputeProof(key, "nonce", "dev-aaaaaaaa", "dev-bbbbbbbb");
        Assert.True(ClipProtocol.VerifyProof(key, "nonce", "dev-aaaaaaaa", "dev-bbbbbbbb", proof));
        Assert.False(ClipProtocol.VerifyProof(key, "nonce", "dev-bbbbbbbb", "dev-aaaaaaaa", proof));
        Assert.False(ClipProtocol.VerifyProof(key, "other", "dev-aaaaaaaa", "dev-bbbbbbbb", proof));
        Assert.False(ClipProtocol.VerifyProof(ClipProtocol.NewKey(), "nonce", "dev-aaaaaaaa", "dev-bbbbbbbb", proof));
    }

    [Fact]
    public async Task ImageCodec_DibToPng_AndBack_KeepsPixels()
    {
        const int w = 3, h = 2;
        // 24 位自下而上 DIB，每行补齐到 4 字节
        int stride = (w * 3 + 3) & ~3;
        var dib = new byte[40 + stride * h];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), h);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 24);
        // 顶行（存储在最后）：红 绿 蓝；底行：白 黑 灰
        byte[][] top = [[0, 0, 255], [0, 255, 0], [255, 0, 0]];
        byte[][] bottom = [[255, 255, 255], [0, 0, 0], [128, 128, 128]];
        for (int x = 0; x < w; x++)
        {
            bottom[x].CopyTo(dib, 40 + x * 3);
            top[x].CopyTo(dib, 40 + stride + x * 3);
        }

        var (png, pw, ph) = await ImageCodec.DibToPngAsync(dib);
        Assert.Equal((w, h), (pw, ph));
        Assert.Equal((w, h), ImageCodec.PngSize(png));
        Assert.False(string.IsNullOrEmpty(await ImageCodec.ThumbnailBase64Async(png)));

        var back = await ImageCodec.PngToDibAsync(png);
        Assert.Equal(h, BinaryPrimitives.ReadInt32LittleEndian(back.AsSpan(8)));
        Assert.Equal(32, BinaryPrimitives.ReadInt16LittleEndian(back.AsSpan(14)));
        // 32 位自下而上：第一行是底行
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, back.AsSpan(40, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, back.AsSpan(40 + w * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, back.AsSpan(40 + w * 4 + 8, 4).ToArray());
    }
}
