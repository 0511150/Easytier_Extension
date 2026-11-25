using ScreenShare.Capture;
using ScreenShare.Media;
using ScreenShare.Streaming;
using Xunit;

namespace EasyTierExt.Tests;

public class ScreenShareCaptureAndResolutionTests
{
    [Fact]
    public void ResolutionPresets_Include16By9And16By10_And_NormalizeLegacyValues()
    {
        Assert.Contains(ResolutionPresets.All, r => r.Key == "original");
        Assert.Contains(ResolutionPresets.All, r => r.Key == "3840x2160" && r.Label.Contains("[16:9]"));
        Assert.Contains(ResolutionPresets.All, r => r.Key == "2560x1440" && r.Label.Contains("[16:9]"));
        Assert.Contains(ResolutionPresets.All, r => r.Key == "1920x1080" && r.Label.Contains("[16:9]"));
        Assert.Contains(ResolutionPresets.All, r => r.Key == "2880x1800" && r.Label.Contains("[16:10]"));
        Assert.Contains(ResolutionPresets.All, r => r.Key == "2560x1600" && r.Label.Contains("[16:10]"));
        Assert.Contains(ResolutionPresets.All, r => r.Key == "1920x1200" && r.Label.Contains("[16:10]"));

        Assert.Equal("1920x1080", ResolutionPresets.Normalize("1080p"));
        Assert.Equal("1280x720", ResolutionPresets.Normalize("720p"));
        Assert.Equal("2560x1600", ResolutionPresets.Normalize("2560x1600"));
        Assert.Equal("original", ResolutionPresets.Normalize("unknown"));

        Assert.Equal((2560, 1600), ResolutionPresets.ResolveTargetSize(1920, 1080, "2560x1600", isWindowCapture: false));
        Assert.Equal((1920, 1080), ResolutionPresets.ResolveTargetSize(1920, 1080, "original", isWindowCapture: false));
        // Window capture always tracks the window's own aspect/dimensions (even-aligned)
        Assert.Equal((1000, 700), ResolutionPresets.ResolveTargetSize(1001, 701, "1920x1080", isWindowCapture: true));
    }

    [Fact]
    public void ColorConvert_ComputeLetterboxFit_PreservesAspectRatioWithoutStretching()
    {
        // 16:9 source (1920x1080) into 16:10 preset (1920x1200) -> vertical letterbox bars
        var (padX1, padY1, fitW1, fitH1) = ColorConvert.ComputeLetterboxFit(1920, 1080, 1920, 1200);
        Assert.Equal(0, padX1);
        Assert.True(padY1 > 0);
        Assert.Equal(1920, fitW1);
        Assert.Equal(1080, fitH1);

        // 16:10 source (2560x1600) into 16:9 preset (1920x1080) -> horizontal pillarbox bars
        var (padX2, padY2, fitW2, fitH2) = ColorConvert.ComputeLetterboxFit(2560, 1600, 1920, 1080);
        Assert.True(padX2 > 0);
        Assert.Equal(0, padY2);
        Assert.Equal(1728, fitW2);
        Assert.Equal(1080, fitH2);

        // Verify BgraToNv12 runs cleanly with letterbox padding
        byte[] src = new byte[1920 * 1080 * 4];
        byte[] dst = new byte[1920 * 1200 * 3 / 2];
        ColorConvert.FillNv12Black(dst, 1920, 1200);
        ColorConvert.BgraToNv12(src, 1920 * 4, 1920, 1080, dst, 1920, 1200);
        // Top letterbox row should remain studio-black Y = 16
        Assert.Equal(16, dst[0]);
    }

    [Fact]
    public void WindowEnumerator_EnumeratesSafely_And_RejectsZeroHwnd()
    {
        var windows = WindowEnumerator.GetCapturableWindows();
        Assert.NotNull(windows);
        Assert.False(WindowEnumerator.IsValidWindow(IntPtr.Zero));
    }
}
