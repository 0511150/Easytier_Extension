using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ScreenShare.Capture;

public static class ThumbnailCapturer
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;

    public static async Task<string?> CaptureWindowJpegBase64Async(IntPtr hwnd, int thumbW = 288, int thumbH = 162)
    {
        if (!WindowEnumerator.IsValidWindow(hwnd) || IsIconic(hwnd) || IsHungAppWindow(hwnd))
        {
            return null;
        }

        try
        {
            if (!GetWindowRect(hwnd, out var rc))
            {
                return null;
            }

            int srcW = rc.Right - rc.Left;
            int srcH = rc.Bottom - rc.Top;
            if (srcW < 16 || srcH < 16)
            {
                return null;
            }

            byte[]? bgra = CaptureWindowBgra(hwnd, srcW, srcH, thumbW, thumbH);
            if (bgra is null)
            {
                return null;
            }

            return await EncodeBgraToJpegBase64Async(bgra, thumbW, thumbH);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> CaptureMonitorJpegBase64Async(CapturableMonitorInfo mon, int thumbW = 288, int thumbH = 162)
    {
        if (mon.Width < 16 || mon.Height < 16)
        {
            return null;
        }

        try
        {
            byte[]? bgra = CaptureRectBgra(mon.Left, mon.Top, mon.Width, mon.Height, thumbW, thumbH);
            if (bgra is null)
            {
                return null;
            }

            return await EncodeBgraToJpegBase64Async(bgra, thumbW, thumbH);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? CaptureWindowBgra(IntPtr hwnd, int srcW, int srcH, int thumbW, int thumbH)
    {
        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return null;

        IntPtr hdcSrc = IntPtr.Zero;
        IntPtr hBmpSrc = IntPtr.Zero;
        IntPtr hOldSrc = IntPtr.Zero;
        IntPtr hdcDst = IntPtr.Zero;
        IntPtr hBmpDst = IntPtr.Zero;
        IntPtr hOldDst = IntPtr.Zero;

        try
        {
            hdcSrc = CreateCompatibleDC(hdcScreen);
            hBmpSrc = CreateCompatibleBitmap(hdcScreen, srcW, srcH);
            if (hdcSrc == IntPtr.Zero || hBmpSrc == IntPtr.Zero) return null;
            hOldSrc = SelectObject(hdcSrc, hBmpSrc);

            if (!PrintWindow(hwnd, hdcSrc, PW_RENDERFULLCONTENT))
            {
                return null;
            }

            return ScaleHdcToBgra(hdcSrc, 0, 0, srcW, srcH, hdcScreen, thumbW, thumbH, ref hdcDst, ref hBmpDst, ref hOldDst);
        }
        finally
        {
            if (hOldDst != IntPtr.Zero && hdcDst != IntPtr.Zero) SelectObject(hdcDst, hOldDst);
            if (hBmpDst != IntPtr.Zero) DeleteObject(hBmpDst);
            if (hdcDst != IntPtr.Zero) DeleteDC(hdcDst);

            if (hOldSrc != IntPtr.Zero && hdcSrc != IntPtr.Zero) SelectObject(hdcSrc, hOldSrc);
            if (hBmpSrc != IntPtr.Zero) DeleteObject(hBmpSrc);
            if (hdcSrc != IntPtr.Zero) DeleteDC(hdcSrc);

            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static byte[]? CaptureRectBgra(int srcX, int srcY, int srcW, int srcH, int thumbW, int thumbH)
    {
        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return null;

        IntPtr hdcDst = IntPtr.Zero;
        IntPtr hBmpDst = IntPtr.Zero;
        IntPtr hOldDst = IntPtr.Zero;

        try
        {
            return ScaleHdcToBgra(hdcScreen, srcX, srcY, srcW, srcH, hdcScreen, thumbW, thumbH, ref hdcDst, ref hBmpDst, ref hOldDst);
        }
        finally
        {
            if (hOldDst != IntPtr.Zero && hdcDst != IntPtr.Zero) SelectObject(hdcDst, hOldDst);
            if (hBmpDst != IntPtr.Zero) DeleteObject(hBmpDst);
            if (hdcDst != IntPtr.Zero) DeleteDC(hdcDst);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static byte[]? ScaleHdcToBgra(
        IntPtr hdcSrc,
        int srcX,
        int srcY,
        int srcW,
        int srcH,
        IntPtr hdcRef,
        int thumbW,
        int thumbH,
        ref IntPtr hdcDst,
        ref IntPtr hBmpDst,
        ref IntPtr hOldDst)
    {
        hdcDst = CreateCompatibleDC(hdcRef);
        if (hdcDst == IntPtr.Zero) return null;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = thumbW,
                biHeight = -thumbH, // Top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        hBmpDst = CreateDIBSection(hdcDst, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
        if (hBmpDst == IntPtr.Zero || pBits == IntPtr.Zero) return null;

        hOldDst = SelectObject(hdcDst, hBmpDst);
        SetStretchBltMode(hdcDst, HALFTONE);
        SetBrushOrgEx(hdcDst, 0, 0, IntPtr.Zero);

        double scale = Math.Min((double)thumbW / srcW, (double)thumbH / srcH);
        int fitW = Math.Max(1, (int)Math.Round(srcW * scale));
        int fitH = Math.Max(1, (int)Math.Round(srcH * scale));
        int dstX = (thumbW - fitW) / 2;
        int dstY = (thumbH - fitH) / 2;

        if (!StretchBlt(hdcDst, dstX, dstY, fitW, fitH, hdcSrc, srcX, srcY, srcW, srcH, SRCCOPY))
        {
            return null;
        }

        int byteCount = thumbW * thumbH * 4;
        var bgra = new byte[byteCount];
        Marshal.Copy(pBits, bgra, 0, byteCount);
        for (int i = 3; i < byteCount; i += 4)
        {
            bgra[i] = 255;
        }
        return bgra;
    }

    private static async Task<string?> EncodeBgraToJpegBase64Async(byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96,
            96,
            bgra);
        await encoder.FlushAsync();

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return Convert.ToBase64String(bytes);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
