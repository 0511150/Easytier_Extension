using System.Buffers.Binary;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ClipboardShare.Clipboard;

/// <summary>剪贴板图片格式转换（Windows 自带的 WIC 编解码器）：CF_DIB ↔ PNG，以及历史列表缩略图。</summary>
public static class ImageCodec
{
    private const int BitmapFileHeaderSize = 14;
    private const uint BI_BITFIELDS = 3;

    /// <summary>CF_DIB（BITMAPINFO + 像素）转 PNG。</summary>
    public static async Task<(byte[] Png, int Width, int Height)> DibToPngAsync(byte[] dib)
    {
        var bmp = DibToBmpFile(dib);
        var (pixels, width, height) = await DecodeAsync(bmp, BitmapDecoder.BmpDecoderId);
        return (await EncodePngAsync(pixels, width, height), width, height);
    }

    /// <summary>PNG 转 32 位自下而上的 CF_DIB，供不认识 PNG 格式的程序粘贴。</summary>
    public static async Task<byte[]> PngToDibAsync(byte[] png)
    {
        var (pixels, width, height) = await DecodeAsync(png, BitmapDecoder.PngDecoderId);
        int stride = width * 4;
        var dib = new byte[40 + stride * height];
        var header = dib.AsSpan(0, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height); // 正数：自下而上
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);
        for (int y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(pixels, y * stride, dib, 40 + (height - 1 - y) * stride, stride);
        }
        return dib;
    }

    /// <summary>读取 PNG 的宽高（IHDR），格式不对时返回 (0, 0)。</summary>
    public static (int Width, int Height) PngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return (0, 0);
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    /// <summary>生成不超过 maxWidth × maxHeight 的 PNG 缩略图（base64）。</summary>
    public static async Task<string> ThumbnailBase64Async(byte[] png, int maxWidth = 360, int maxHeight = 200)
    {
        using var input = new MemoryStream(png, writable: false);
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, input.AsRandomAccessStream());
        double scale = Math.Min(1.0, Math.Min(maxWidth / (double)decoder.PixelWidth, maxHeight / (double)decoder.PixelHeight));
        uint w = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
        uint h = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return Convert.ToBase64String(await EncodePngAsync(data.DetachPixelData(), (int)w, (int)h));
    }

    private static async Task<(byte[] Pixels, int Width, int Height)> DecodeAsync(byte[] data, Guid decoderId)
    {
        using var input = new MemoryStream(data, writable: false);
        var decoder = await BitmapDecoder.CreateAsync(decoderId, input.AsRandomAccessStream());
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (pixels.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    private static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>在 DIB 前补上 BITMAPFILEHEADER，交给 WIC 的 BMP 解码器（支持各种位深、调色板与位域）。</summary>
    private static byte[] DibToBmpFile(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidDataException("DIB 数据过短");
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        int colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
        if (headerSize < 40 || headerSize > dib.Length) throw new InvalidDataException("DIB 头无效");

        int masks = compression == BI_BITFIELDS && headerSize == 40 ? 12 : 0;
        int colors = colorsUsed > 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        int pixelOffset = BitmapFileHeaderSize + headerSize + masks + colors * 4;

        var bmp = new byte[BitmapFileHeaderSize + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), pixelOffset);
        dib.CopyTo(bmp, BitmapFileHeaderSize);
        return bmp;
    }
}
