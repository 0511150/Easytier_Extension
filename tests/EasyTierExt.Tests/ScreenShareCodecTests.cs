using System.Runtime.InteropServices;
using ScreenShare.Media;
using ScreenShare.Streaming;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace EasyTierExt.Tests;

public class ScreenShareCodecTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // 640x360：高度不是 16 的倍数，解码器输出 368 行，可以验证显示区域裁剪
    private const int W = 640;
    private const int H = 360;
    private const byte Y = 120;
    private const byte U = 70;
    private const byte V = 190;

    [Fact]
    public void EncoderCandidates_PreferHevcHardware_ThenH264HardwareThenSoftware()
    {
        Assert.Equal(
            [(VideoCodec.Hevc, true), (VideoCodec.H264, true), (VideoCodec.H264, false)],
            ScreenShareServer.EncoderCandidates(VideoCodec.Hevc));
        Assert.Equal(
            [(VideoCodec.H264, true), (VideoCodec.H264, false)],
            ScreenShareServer.EncoderCandidates(VideoCodec.H264));
    }

    [Fact]
    public void PacketTypes_KeepH264CompatibleWithOldViewers()
    {
        Assert.Equal(0x01, VideoCodecs.ToPacketType(VideoCodec.H264));
        Assert.Equal(0x03, VideoCodecs.ToPacketType(VideoCodec.Hevc));
        Assert.Equal(VideoCodec.Hevc, VideoCodecs.FromPacketType(0x03));
        Assert.Null(VideoCodecs.FromPacketType(0x02)); // 音频
        Assert.Equal(VideoCodec.H264, VideoCodecs.FromWire(null));
    }

    [Fact]
    public void ContainsParameterSets_DetectsSpsAndVps()
    {
        byte[] h264Idr = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 1, 0x68, 0xCE, 0, 0, 1, 0x65, 0x88];
        byte[] h264P = [0, 0, 0, 1, 0x41, 0x9A];
        byte[] hevcIdr = [0, 0, 0, 1, 0x40, 0x01, 0x0C, 0, 0, 1, 0x26, 0x01];
        byte[] hevcIdrNoVps = [0, 0, 0, 1, 0x26, 0x01];

        Assert.True(VideoCodecs.ContainsParameterSets(VideoCodec.H264, h264Idr));
        Assert.False(VideoCodecs.ContainsParameterSets(VideoCodec.H264, h264P));
        Assert.True(VideoCodecs.ContainsParameterSets(VideoCodec.Hevc, hevcIdr));
        Assert.False(VideoCodecs.ContainsParameterSets(VideoCodec.Hevc, hevcIdrNoVps));
    }

    [Fact]
    public void SoftwareH264_RoundTrip_DeliversCroppedNv12InMemory()
    {
        var frames = EncodeSolidFrames(VideoCodec.H264, hardware: false);
        Assert.NotEmpty(frames);
        Assert.True(VideoCodecs.ContainsParameterSets(VideoCodec.H264, frames[0]), "首帧应是带 SPS 的关键帧");

        var sink = new CapturingSink();
        using var decoder = MfVideoDecoder.Create(VideoCodec.H264, W, H, IntPtr.Zero, requireD3D11: false);
        int presented = frames.Select((f, i) => decoder.Decode(f, i * 333_333L, sink)).Sum();

        Assert.True(presented > 0, "软件解码没有输出画面");
        Assert.False(decoder.IsHardwareOutput);
        Assert.Equal(0, sink.TextureFrames);
        Assert.Equal((W, H), (sink.Width, sink.Height));
        AssertColor(sink.CenterY, sink.CenterU, sink.CenterV);
    }

    [Fact]
    public void H264_WithD3D11Device_DecodesThroughDxgiOrFallsBackToMemory()
    {
        // 这台机器若没有可用的视频设备（如部分虚拟机），只验证不会崩
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? device).Failure || device is null)
        {
            output.WriteLine("跳过：无法创建带视频支持的 D3D11 设备");
            return;
        }

        using (device)
        {
            using (var mt = device.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                mt?.SetMultithreadProtected(true);
            }

            MfVideoDecoder decoder;
            try
            {
                decoder = MfVideoDecoder.Create(VideoCodec.H264, W, H, device.NativePointer, requireD3D11: true);
            }
            catch (NotSupportedException)
            {
                output.WriteLine("跳过：解码器不接受 D3D11 设备");
                return;
            }

            var frames = EncodeSolidFrames(VideoCodec.H264, hardware: false);
            var sink = new CapturingSink(device);
            using (decoder)
            {
                int presented = frames.Select((f, i) => decoder.Decode(f, i * 333_333L, sink)).Sum();
                Assert.True(presented > 0, "D3D11 模式下没有输出画面");
                Assert.Equal(decoder.IsHardwareOutput, sink.TextureFrames > 0);
                output.WriteLine($"D3D11 模式：{presented} 帧，其中显存纹理 {sink.TextureFrames} 帧");
            }

            Assert.Equal((W, H), (sink.Width, sink.Height));
            AssertColor(sink.CenterY, sink.CenterU, sink.CenterV);
        }
    }

    [Fact]
    public void HardwareEncoders_WhenPresent_ProduceDecodableStream()
    {
        // 有硬件编码器的机器上验证异步 MFT 路径；没有就跳过
        foreach (var codec in new[] { VideoCodec.Hevc, VideoCodec.H264 })
        {
            List<byte[]> frames;
            try
            {
                frames = EncodeSolidFrames(codec, hardware: true);
            }
            catch (SkipException)
            {
                continue;
            }

            Assert.NotEmpty(frames);
            Assert.True(VideoCodecs.ContainsParameterSets(codec, frames[0]), $"{codec} 首帧缺少参数集");
        }
    }

    private static List<byte[]> EncodeSolidFrames(VideoCodec codec, bool hardware)
    {
        var output = new List<byte[]>();
        var gotOutput = new ManualResetEventSlim();
        using var encoder = MfVideoEncoder.TryCreate(codec, hardware, W, H, 30, 4, (data, _) =>
        {
            lock (output) output.Add(data);
            gotOutput.Set();
        });
        if (encoder is null)
        {
            if (hardware) throw new SkipException();
            Assert.Fail($"没有可用的软件 {codec} 编码器");
        }

        Assert.Equal(hardware, encoder!.IsHardware);
        FillSolid(encoder.Nv12Buffer, encoder.Width, encoder.Height);
        for (int i = 0; i < 15; i++)
        {
            encoder.EncodeNv12(i * 333_333L, 333_333L);
            if (hardware) Thread.Sleep(30); // 异步编码器在事件线程上输出
        }
        if (hardware) gotOutput.Wait(TimeSpan.FromSeconds(2));
        Assert.False(encoder.IsFaulted);
        lock (output) return [.. output];
    }

    private static void FillSolid(byte[] nv12, int width, int height)
    {
        Array.Fill(nv12, Y, 0, width * height);
        for (int i = width * height; i + 1 < nv12.Length; i += 2)
        {
            nv12[i] = U;
            nv12[i + 1] = V;
        }
    }

    private static void AssertColor(byte y, byte u, byte v)
    {
        Assert.InRange(y, Y - 8, Y + 8);
        Assert.InRange(u, U - 8, U + 8);
        Assert.InRange(v, V - 8, V + 8);
    }

    private sealed class SkipException : Exception;

    private sealed class CapturingSink(ID3D11Device? device = null) : IVideoFrameSink
    {
        public int TextureFrames;
        public int Width;
        public int Height;
        public byte CenterY;
        public byte CenterU;
        public byte CenterV;

        public void PresentNv12Memory(IntPtr data, int stride, int planeHeight, int width, int height)
        {
            Width = width;
            Height = height;
            int cx = width / 2 & ~1;
            int cy = height / 2 & ~1;
            CenterY = Marshal.ReadByte(data, cy * stride + cx);
            int uv = stride * planeHeight + cy / 2 * stride + cx;
            CenterU = Marshal.ReadByte(data, uv);
            CenterV = Marshal.ReadByte(data, uv + 1);
        }

        public void PresentNv12Texture(IntPtr texture, uint subresource, int width, int height)
        {
            TextureFrames++;
            Width = width;
            Height = height;
            Assert.NotNull(device);

            Marshal.AddRef(texture);
            using var src = new ID3D11Texture2D(texture);
            using var staging = device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read
            });
            var ctx = device.ImmediateContext;
            ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, src, subresource, new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
            var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                PresentNv12Memory(mapped.DataPointer, (int)mapped.RowPitch, height, width, height);
                Width = width;
                Height = height;
            }
            finally
            {
                ctx.Unmap(staging, 0);
            }
        }
    }
}
