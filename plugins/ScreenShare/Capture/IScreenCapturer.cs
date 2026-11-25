using Vortice.Direct3D11;

namespace ScreenShare.Capture;

public interface IScreenCapturer : IDisposable
{
    int Width { get; }
    int Height { get; }
    bool IsTargetClosed { get; }
    bool TryAcquireFrame(out ID3D11Texture2D? gpuTexture);
}
