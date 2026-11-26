using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace MouseShare.Engine;

/// <summary>与一台已配对设备之间认证完成的连接。发送不阻塞：帧先进入队列，由写线程合并后发出。</summary>
public sealed class PeerSession
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private long _lastReceived = Environment.TickCount64;
    private int _closed;

    internal PeerSession(string peerId, bool isDialer, TcpClient client, NetworkStream stream)
    {
        PeerId = peerId;
        IsDialer = isDialer;
        _client = client;
        _stream = stream;
        Remote = client.Client.RemoteEndPoint as IPEndPoint;
    }

    public string PeerId { get; }
    /// <summary>本机是否为发起连接的一方。</summary>
    public bool IsDialer { get; }
    public IPEndPoint? Remote { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public long IdleMs => Environment.TickCount64 - Interlocked.Read(ref _lastReceived);

    internal void Start(Action<PeerSession, MsgType, byte[]> onMessage, Action<PeerSession> onClosed)
    {
        _ = Task.Run(() => ReadLoopAsync(onMessage, onClosed));
        _ = Task.Run(WriteLoopAsync);
    }

    public bool Send(byte[] frame) => !IsClosed && _out.Writer.TryWrite(frame);

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _out.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { _client.Close(); } catch { }
    }

    private async Task ReadLoopAsync(Action<PeerSession, MsgType, byte[]> onMessage, Action<PeerSession> onClosed)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (type, payload) = await Protocol.ReadFrameAsync(_stream, _cts.Token);
                Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
                try
                {
                    onMessage(this, type, payload);
                }
                catch
                {
                    // 单条消息处理失败不影响连接
                }
            }
        }
        catch
        {
        }
        finally
        {
            Close();
            onClosed(this);
        }
    }

    private async Task WriteLoopAsync()
    {
        var buffer = new MemoryStream();
        var batch = new List<byte[]>();
        try
        {
            var reader = _out.Reader;
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                batch.Clear();
                while (batch.Count < 512 && reader.TryRead(out var f)) batch.Add(f);

                buffer.SetLength(0);
                for (int i = 0; i < batch.Count; i++)
                {
                    // 连续的光标移动只发最后一个（绝对坐标，丢弃中间点不影响结果）
                    if (Protocol.TypeOf(batch[i]) == MsgType.MouseMove && i + 1 < batch.Count && Protocol.TypeOf(batch[i + 1]) == MsgType.MouseMove)
                    {
                        continue;
                    }
                    buffer.Write(batch[i]);
                }
                await _stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), _cts.Token);
                await _stream.FlushAsync(_cts.Token);
            }
        }
        catch
        {
            Close();
        }
    }
}
