using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenShare.Capture;

public sealed class DxgiScreenCapturer : IScreenCapturer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly string? _targetDeviceName;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _copyTexture;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsTargetClosed => false;

    public DxgiScreenCapturer(ID3D11Device device, string? targetDeviceName = null)
    {
        _device = device;
        _context = device.ImmediateContext;
        _targetDeviceName = targetDeviceName;
        InitializeDuplication();
    }

    private void InitializeDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        IDXGIOutput? selectedOutput = null;
        for (uint idx = 0; adapter.EnumOutputs(idx, out var candidate).Success; idx++)
        {
            if (selectedOutput is null)
            {
                selectedOutput = candidate;
            }
            else if (!string.IsNullOrWhiteSpace(_targetDeviceName) &&
                     string.Equals(candidate.Description.DeviceName, _targetDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                selectedOutput.Dispose();
                selectedOutput = candidate;
                break;
            }
            else
            {
                candidate.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(_targetDeviceName) &&
                string.Equals(selectedOutput.Description.DeviceName, _targetDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        if (selectedOutput is null)
        {
            adapter.EnumOutputs(0, out selectedOutput).CheckError();
        }

        using (selectedOutput)
        {
            using var output1 = selectedOutput.QueryInterface<IDXGIOutput1>();
            var desc = selectedOutput.Description;
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
