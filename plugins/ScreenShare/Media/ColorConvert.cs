namespace ScreenShare.Media;

public static unsafe class ColorConvert
{
    public static (int padX, int padY, int fitW, int fitH) ComputeLetterboxFit(
        int srcWidth,
        int srcHeight,
        int dstWidth,
        int dstHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
        {
            return (0, 0, Math.Max(2, dstWidth & ~1), Math.Max(2, dstHeight & ~1));
        }

        double srcAspect = (double)srcWidth / srcHeight;
        double dstAspect = (double)dstWidth / dstHeight;

        int fitW;
        int fitH;
        if (Math.Abs(srcAspect - dstAspect) < 0.002)
        {
            fitW = dstWidth;
            fitH = dstHeight;
        }
        else if (dstAspect > srcAspect)
        {
            fitH = dstHeight;
            fitW = (int)Math.Round(fitH * srcAspect);
        }
        else
        {
            fitW = dstWidth;
            fitH = (int)Math.Round(fitW / srcAspect);
        }

        fitW = Math.Clamp(fitW & ~1, 2, dstWidth & ~1);
        fitH = Math.Clamp(fitH & ~1, 2, dstHeight & ~1);
        int padX = ((dstWidth - fitW) / 2) & ~1;
        int padY = ((dstHeight - fitH) / 2) & ~1;
        return (padX, padY, fitW, fitH);
    }

    public static void FillNv12Black(byte[] dstNv12, int dstWidth, int dstHeight)
    {
        int ySize = dstWidth * dstHeight;
        if (dstNv12.Length < ySize + (ySize / 2)) return;
        Array.Fill(dstNv12, (byte)16, 0, ySize);
        Array.Fill(dstNv12, (byte)128, ySize, dstNv12.Length - ySize);
    }

    public static void BgraToNv12(
        byte[] srcBgra,
        int srcStride,
        int srcWidth,
        int srcHeight,
        byte[] dstNv12,
        int dstWidth,
        int dstHeight)
    {
        fixed (byte* pSrc = srcBgra)
        {
            BgraToNv12(pSrc, srcStride, srcWidth, srcHeight, dstNv12, dstWidth, dstHeight);
        }
    }

    public static void BgraToNv12(
        byte* srcBgra,
        int srcStride,
        int srcWidth,
        int srcHeight,
        byte[] dstNv12,
        int dstWidth,
        int dstHeight)
    {
        var (padX, padY, fitW, fitH) = ComputeLetterboxFit(srcWidth, srcHeight, dstWidth, dstHeight);
        int yPlaneSize = dstWidth * dstHeight;

        fixed (byte* dst = dstNv12)
        {
            byte* yPlane = dst;
            byte* uvPlane = dst + yPlaneSize;

            for (int fy = 0; fy < fitH; fy++)
            {
                int dy = padY + fy;
                int sy = fy * srcHeight / fitH;
                byte* srcRow = srcBgra + sy * srcStride;
                byte* yRow = yPlane + dy * dstWidth + padX;
                bool isEvenRow = (dy & 1) == 0;
                byte* uvRow = isEvenRow ? uvPlane + (dy / 2) * dstWidth + padX : null;

                for (int fx = 0; fx < fitW; fx++)
                {
                    int sx = fx * srcWidth / fitW;
                    byte* px = srcRow + sx * 4;
                    int b = px[0];
                    int g = px[1];
                    int r = px[2];

                    int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                    yRow[fx] = (byte)Math.Clamp(y, 0, 255);

                    if (isEvenRow && (fx & 1) == 0)
                    {
                        int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                        uvRow[fx] = (byte)Math.Clamp(u, 0, 255);
                        uvRow[fx + 1] = (byte)Math.Clamp(v, 0, 255);
                    }
                }
            }
        }
    }
}
