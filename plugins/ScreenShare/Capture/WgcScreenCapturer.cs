using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenShare.Capture;

public sealed class WgcScreenCapturer : IScreenCapturer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly IntPtr _targetWindowHwnd;
    private ID3D11Texture2D _copyTexture;
    private bool _hasFrame;
    private volatile bool _targetClosed;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsTargetClosed => _targetClosed || (_targetWindowHwnd != IntPtr.Zero && !WindowEnumerator.IsValidWindow(_targetWindowHwnd));

    public WgcScreenCapturer(ID3D11Device device, IntPtr targetWindowHwnd = default)
    {
        _device = device;
        _context = device.ImmediateContext;
        _targetWindowHwnd = targetWindowHwnd;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pInspectable);
        Marshal.ThrowExceptionForHR(hr);
        _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pInspectable);
        Marshal.Release(pInspectable);

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid itemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        IntPtr pItem;
        if (targetWindowHwnd != IntPtr.Zero)
        {
            if (!WindowEnumerator.IsValidWindow(targetWindowHwnd))
            {
                throw new InvalidOperationException("目标窗口不存在或已关闭。");
            }
            pItem = interop.CreateForWindow(targetWindowHwnd, ref itemGuid);
        }
        else
        {
            IntPtr hMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            pItem = interop.CreateForMonitor(hMonitor, ref itemGuid);
        }

        _item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(pItem);
        Marshal.Release(pItem);
        _item.Closed += (_, _) => _targetClosed = true;

        Width = Math.Max(2, _item.Size.Width);
        Height = Math.Max(2, _item.Size.Height);

        _copyTexture = CreateCopyTexture(Width, Height);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _item.Size);
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        _session.StartCapture();
    }

    private ID3D11Texture2D CreateCopyTexture(int width, int height) =>
        _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });

    public bool TryAcquireFrame(out ID3D11Texture2D? gpuTexture)
    {
        gpuTexture = _copyTexture;
        if (_disposed || IsTargetClosed) return false;

        using var frame = _framePool.TryGetNextFrame();
        if (frame is null)
        {
            return _hasFrame;
        }

        var contentSize = frame.ContentSize;
        if (contentSize.Width >= 2 && contentSize.Height >= 2 &&
            (contentSize.Width != Width || contentSize.Height != Height))
        {
            _framePool.Recreate(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                contentSize);
        }

        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid texGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        IntPtr pTex = access.GetInterface(ref texGuid);
        using var srcTex = new ID3D11Texture2D(pTex);

        var desc = srcTex.Description;
        int texW = (int)desc.Width;
        int texH = (int)desc.Height;
        if (texW >= 2 && texH >= 2 && (texW != Width || texH != Height))
        {
            _copyTexture.Dispose();
            Width = texW;
            Height = texH;
            _copyTexture = CreateCopyTexture(texW, texH);
            gpuTexture = _copyTexture;
        }

        _context.CopyResource(_copyTexture, srcTex);
        _hasFrame = true;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _session.Dispose(); } catch { }
        try { _framePool.Dispose(); } catch { }
        _copyTexture.Dispose();
        (_winrtDevice as IDisposable)?.Dispose();
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = false)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
