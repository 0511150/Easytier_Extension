using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenShare.Capture;

public sealed class DxgiScreenCapturer : IScreenCapturer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _copyTexture;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsTargetClosed => false;

    public DxgiScreenCapturer(ID3D11Device device)
    {
        _device = device;
        _context = device.ImmediateContext;
        InitializeDuplication();
    }

    private void InitializeDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs(0, out var output).CheckError();
        using (output)
        {
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            var desc = output.Description;
            Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            _duplication = output1.DuplicateOutput(_device);
        }

        _copyTexture?.Dispose();
        _copyTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });
    }

    public bool TryAcquireFrame(out ID3D11Texture2D? gpuTexture)
    {
        gpuTexture = _copyTexture;
        if (_disposed || _duplication is null || _copyTexture is null)
        {
            return false;
        }

        var result = _duplication.AcquireNextFrame(16, out var frameInfo, out var desktopResource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return frameInfo.LastPresentTime > 0;
        }

        if (result == Vortice.DXGI.ResultCode.AccessLost || result.Failure)
        {
            try { InitializeDuplication(); } catch { }
            return false;
        }

        try
        {
            using (desktopResource)
            using (var srcTex = desktopResource.QueryInterface<ID3D11Texture2D>())
            {
                _context.CopyResource(_copyTexture, srcTex);
            }
            return true;
        }
        finally
        {
            try { _duplication.ReleaseFrame(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _copyTexture?.Dispose();
        _duplication?.Dispose();
    }
}
