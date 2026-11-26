using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ScreenShare.Media;

namespace ScreenShare.Viewer;

public sealed class EmbeddedD3D11Renderer : IVideoFrameSink, IDisposable
{
    private const uint WM_APP_RESIZE = 0x8001;
    private const uint WM_APP_SHUTDOWN = 0x8002;
    private const int WS_EX_NOPARENTNOTIFY = 0x00000004;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    private const string ShaderSource = @"
struct VSOut {
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};
VSOut VSMain(uint id : SV_VertexID) {
    VSOut o;
    float2 pos[4] = { float2(-1, 1), float2(1, 1), float2(-1, -1), float2(1, -1) };
    float2 uv[4]  = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
    o.pos = float4(pos[id], 0, 1);
    o.uv  = uv[id];
    return o;
}
Texture2D texY  : register(t0);
Texture2D texUV : register(t1);
SamplerState sam0 : register(s0);
float4 PSMain(VSOut i) : SV_TARGET {
    // NV12 -> RGB，BT.601 有限范围，与发送端 BgraToNv12 一致
    float y = (texY.Sample(sam0, i.uv).r - 16.0 / 255.0) * 1.164383;
    float2 c = texUV.Sample(sam0, i.uv).rg - 0.5;
    float3 rgb = float3(y + 1.596027 * c.y,
                        y - 0.391762 * c.x - 0.812968 * c.y,
                        y + 2.017232 * c.x);
    return float4(saturate(rgb), 1);
}";

    private readonly IntPtr _hostSlotHwnd;
    private readonly ManualResetEventSlim _readyEvent = new(false);
    private readonly Thread _uiThread;
    private readonly object _renderLock = new();
    private readonly object _stateLock = new();

    private IntPtr _hwnd;
    private int _winW;
    private int _winH;
    private int _pendingW;
    private int _pendingH;
    private bool _pendingVisible;
    private int _videoW;
    private int _videoH;
    private bool _hasVideoFrame;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _sampler;
    // 硬件解码：解码器纹理复制到这张 NV12 纹理，两个平面各一个 SRV
    private ID3D11Texture2D? _nv12Tex;
    private ID3D11ShaderResourceView? _nv12SrvY;
    private ID3D11ShaderResourceView? _nv12SrvUV;
    // 软件解码：Y / UV 两张动态纹理
    private ID3D11Texture2D? _yTex;
    private ID3D11Texture2D? _uvTex;
    private ID3D11ShaderResourceView? _ySrv;
    private ID3D11ShaderResourceView? _uvSrv;
    private bool _lastFrameFromNv12Tex;
    private bool _videoSupport;
    private bool _nv12Sampleable;
    private volatile bool _disposed;

    private WndProcDelegate? _wndProc;

    public bool ControlEnabled { get; set; }
    public event Action<double, double, uint, int>? MouseEvent;
    public event Action<ushort, bool, bool>? KeyEvent;
    public event Action? ControlEscapePressed;

    public EmbeddedD3D11Renderer(IntPtr hostSlotHwnd, int initialWidth, int initialHeight, bool initialVisible = false)
    {
        _hostSlotHwnd = hostSlotHwnd;
        _winW = Math.Max(1, initialWidth);
        _winH = Math.Max(1, initialHeight);
        _pendingW = _winW;
        _pendingH = _winH;
        _pendingVisible = initialVisible;

        _uiThread = new Thread(UiThreadProc) { IsBackground = true };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _readyEvent.Wait(TimeSpan.FromSeconds(5));
    }

    public void Resize(int width, int height, bool visible)
    {
        if (_hwnd == IntPtr.Zero || _disposed) return;
        lock (_stateLock)
        {
            _pendingW = Math.Max(1, width);
            _pendingH = Math.Max(1, height);
            _pendingVisible = visible;
        }
        PostMessageW(_hwnd, WM_APP_RESIZE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>供硬件解码器使用的 D3D11 设备（未启用视频支持或无法采样 NV12 时为 0）。</summary>
    public IntPtr DecoderDevice => _videoSupport && _nv12Sampleable && _device is not null ? _device.NativePointer : IntPtr.Zero;

    /// <summary>显卡是否有该 DXVA 解码配置（且能输出 NV12）。</summary>
    public bool SupportsDecoderProfile(Guid profile)
    {
        if (DecoderDevice == IntPtr.Zero || _device is null) return false;
        try
        {
            using var videoDevice = _device.QueryInterfaceOrNull<ID3D11VideoDevice>();
            if (videoDevice is null) return false;
            uint count = videoDevice.VideoDecoderProfileCount;
            for (uint i = 0; i < count; i++)
            {
                videoDevice.GetVideoDecoderProfile(i, out var p).CheckError();
                if (p != profile) continue;
                videoDevice.CheckVideoDecoderFormat(profile, Format.NV12, out var supported).CheckError();
                return supported;
            }
        }
        catch
        {
        }
        return false;
    }

    public void PresentNv12Texture(IntPtr texture, uint subresource, int width, int height)
    {
        if (_disposed || texture == IntPtr.Zero) return;
        lock (_renderLock)
        {
            if (_disposed || _device is null || _context is null) return;
            // 这个引用归解码器所有：先 AddRef，包装对象 Dispose 时再 Release
            Marshal.AddRef(texture);
            using var src = new ID3D11Texture2D(texture);
            var srcDesc = src.Description;
            width = Math.Min(width, (int)srcDesc.Width) & ~1;
            height = Math.Min(height, (int)srcDesc.Height) & ~1;
            if (width <= 0 || height <= 0) return;

            if (_nv12Tex is null || _videoW != width || _videoH != height || !_lastFrameFromNv12Tex)
            {
                ReleaseVideoTextures();
                _videoW = width;
                _videoH = height;
                _nv12Tex = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource
                });
                _nv12SrvY = _device.CreateShaderResourceView(_nv12Tex,
                    new ShaderResourceViewDescription(_nv12Tex, ShaderResourceViewDimension.Texture2D, Format.R8_UNorm));
                _nv12SrvUV = _device.CreateShaderResourceView(_nv12Tex,
                    new ShaderResourceViewDescription(_nv12Tex, ShaderResourceViewDimension.Texture2D, Format.R8G8_UNorm));
                _lastFrameFromNv12Tex = true;
            }

            _context.CopySubresourceRegion(_nv12Tex, 0, 0, 0, 0, src, subresource, new Box(0, 0, 0, width, height, 1));
            _hasVideoFrame = true;
            DrawFrameLocked();
        }
    }

    public unsafe void PresentNv12Memory(IntPtr data, int stride, int planeHeight, int width, int height)
    {
        if (_disposed || data == IntPtr.Zero) return;
        lock (_renderLock)
        {
            if (_disposed || _device is null || _context is null) return;
            width &= ~1;
            height &= ~1;
            if (width <= 0 || height <= 0) return;

            if (_yTex is null || _videoW != width || _videoH != height || _lastFrameFromNv12Tex)
            {
                ReleaseVideoTextures();
                _videoW = width;
                _videoH = height;
                _yTex = CreateDynamicTexture(width, height, Format.R8_UNorm);
                _uvTex = CreateDynamicTexture(width / 2, height / 2, Format.R8G8_UNorm);
                _ySrv = _device.CreateShaderResourceView(_yTex);
                _uvSrv = _device.CreateShaderResourceView(_uvTex);
                _lastFrameFromNv12Tex = false;
            }

            byte* src = (byte*)data;
            UploadPlane(_yTex, src, stride, width, height);
            UploadPlane(_uvTex!, src + (long)stride * planeHeight, stride, width, height / 2);
            _hasVideoFrame = true;
            DrawFrameLocked();
        }
    }

    private ID3D11Texture2D CreateDynamicTexture(int width, int height, Format format) =>
        _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        });

    private unsafe void UploadPlane(ID3D11Texture2D tex, byte* src, int srcStride, int rowBytes, int rows)
    {
        var mapped = _context!.Map(tex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* dst = (byte*)mapped.DataPointer;
            for (int y = 0; y < rows; y++)
            {
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * mapped.RowPitch, rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(tex, 0);
        }
    }

    private void ReleaseVideoTextures()
    {
        _nv12SrvY?.Dispose();
        _nv12SrvY = null;
        _nv12SrvUV?.Dispose();
        _nv12SrvUV = null;
        _nv12Tex?.Dispose();
        _nv12Tex = null;
        _ySrv?.Dispose();
        _ySrv = null;
        _uvSrv?.Dispose();
        _uvSrv = null;
        _yTex?.Dispose();
        _yTex = null;
        _uvTex?.Dispose();
        _uvTex = null;
    }

    /// <summary>在 _renderLock 内调用：有画面就按比例居中绘制，没有就清屏。</summary>
    private void DrawFrameLocked()
    {
        if (_context is null || _rtv is null || _swapChain is null) return;
        _context.OMSetRenderTargets(_rtv);
        _context.ClearRenderTargetView(_rtv, new Color4(0.05f, 0.05f, 0.06f, 1f));

        var srvY = _lastFrameFromNv12Tex ? _nv12SrvY : _ySrv;
        var srvUV = _lastFrameFromNv12Tex ? _nv12SrvUV : _uvSrv;
        if (_hasVideoFrame && srvY is not null && srvUV is not null)
        {
            var (vx, vy, vw, vh) = ComputeLetterboxRect();
            _context.RSSetViewport(new Viewport(vx, vy, vw, vh, 0f, 1f));
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetShaderResources(0, [srvY, srvUV]);
            _context.PSSetSampler(0, _sampler);
            _context.Draw(4, 0);
        }
        _swapChain.Present(0, PresentFlags.None);
    }

    private (float x, float y, float w, float h) ComputeLetterboxRect()
    {
        if (_videoW <= 0 || _videoH <= 0) return (0, 0, _winW, _winH);
        float winAspect = (float)_winW / _winH;
        float vidAspect = (float)_videoW / _videoH;
        if (winAspect > vidAspect)
        {
            float h = _winH;
            float w = h * vidAspect;
            return ((_winW - w) * 0.5f, 0f, w, h);
        }
        else
        {
            float w = _winW;
            float h = w / vidAspect;
            return (0f, (_winH - h) * 0.5f, w, h);
        }
    }

    private void UiThreadProc()
    {
        try
        {
            _wndProc = WndProc;
            var hInst = GetModuleHandleW(null);
            var cls = $"EasyTierScreenShareView_{Environment.TickCount64}";
            var wc = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                lpszClassName = cls
            };
            RegisterClassExW(ref wc);

            int style = WS_CHILD | (_pendingVisible ? WS_VISIBLE : 0);
            _hwnd = CreateWindowExW(
                WS_EX_NOPARENTNOTIFY,
                cls,
                "",
                style,
                0, 0, _winW, _winH,
                _hostSlotHwnd,
                IntPtr.Zero,
                hInst,
                IntPtr.Zero);

            InitD3D11();
        }
        finally
        {
            _readyEvent.Set();
        }

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private void InitD3D11()
    {
        // VideoSupport 是 DXVA 硬件解码的前提；个别驱动不支持时退回普通设备（软件解码）
        _videoSupport = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out _device).Success;
        if (!_videoSupport)
        {
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0],
                out _device).CheckError();
        }
        _context = _device!.ImmediateContext;

        // 解码器在自己的线程里用同一设备，必须开启多线程保护
        using (var multithread = _device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            multithread?.SetMultithreadProtected(true);
        }
        _nv12Sampleable = _videoSupport && CanSampleNv12(_device);

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, new SwapChainDescription1
        {
            Width = (uint)_winW,
            Height = (uint)_winH,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipDiscard
        });

        CreateRtv();

        var vsBytecode = Compiler.Compile(ShaderSource, "VSMain", "shader.hlsl", "vs_5_0");
        var psBytecode = Compiler.Compile(ShaderSource, "PSMain", "shader.hlsl", "ps_5_0");
        _vs = _device.CreateVertexShader(vsBytecode.Span);
        _ps = _device.CreatePixelShader(psBytecode.Span);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);

        if (_rtv is not null)
        {
            _context.OMSetRenderTargets(_rtv);
            _context.ClearRenderTargetView(_rtv, new Color4(0.05f, 0.05f, 0.06f, 1f));
            _swapChain.Present(0, PresentFlags.None);
        }
    }

    /// <summary>实际建一张 NV12 纹理和两个平面视图，成功才走硬件解码（比查格式支持位可靠）。</summary>
    private static bool CanSampleNv12(ID3D11Device device)
    {
        try
        {
            using var tex = device.CreateTexture2D(new Texture2DDescription
            {
                Width = 16,
                Height = 16,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource
            });
            using var y = device.CreateShaderResourceView(tex,
                new ShaderResourceViewDescription(tex, ShaderResourceViewDimension.Texture2D, Format.R8_UNorm));
            using var uv = device.CreateShaderResourceView(tex,
                new ShaderResourceViewDescription(tex, ShaderResourceViewDimension.Texture2D, Format.R8G8_UNorm));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CreateRtv()
    {
        if (_device is null || _swapChain is null) return;
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);
    }

    private void ApplyPendingResizeOnUiThread()
    {
        int w, h;
        bool vis;
        lock (_stateLock)
        {
            w = _pendingW;
            h = _pendingH;
            vis = _pendingVisible;
        }

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        ShowWindowAsync(_hwnd, vis ? 5 : 0);

        lock (_renderLock)
        {
            if (w != _winW || h != _winH)
            {
                _winW = w;
                _winH = h;
                if (_swapChain is not null)
                {
                    _rtv?.Dispose();
                    _rtv = null;
                    _swapChain.ResizeBuffers(2, (uint)_winW, (uint)_winH, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                    CreateRtv();
                }
            }

            if (vis)
            {
                DrawFrameLocked();
            }
        }
    }

    private void CleanupOnUiThread()
    {
        lock (_renderLock)
        {
            ReleaseVideoTextures();
            _sampler?.Dispose();
            _sampler = null;
            _ps?.Dispose();
            _ps = null;
            _vs?.Dispose();
            _vs = null;
            _rtv?.Dispose();
            _rtv = null;
            _swapChain?.Dispose();
            _swapChain = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        PostQuitMessage(0);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_RESIZE)
        {
            ApplyPendingResizeOnUiThread();
            return IntPtr.Zero;
        }

        if (msg == WM_APP_SHUTDOWN)
        {
            CleanupOnUiThread();
            return IntPtr.Zero;
        }

        if (ControlEnabled && !_disposed)
        {
            switch (msg)
            {
                case 0x0200: // WM_MOUSEMOVE
                    EmitMouse(lParam, 0x0001, 0);
                    break;
                case 0x0201: // WM_LBUTTONDOWN
                    SetFocus(hWnd);
                    EmitMouse(lParam, 0x0002, 0);
                    break;
                case 0x0202: // WM_LBUTTONUP
                    EmitMouse(lParam, 0x0004, 0);
                    break;
                case 0x0204: // WM_RBUTTONDOWN
                    SetFocus(hWnd);
                    EmitMouse(lParam, 0x0008, 0);
                    break;
                case 0x0205: // WM_RBUTTONUP
                    EmitMouse(lParam, 0x0010, 0);
                    break;
                case 0x0207: // WM_MBUTTONDOWN
                    EmitMouse(lParam, 0x0020, 0);
                    break;
                case 0x0208: // WM_MBUTTONUP
                    EmitMouse(lParam, 0x0040, 0);
                    break;
                case 0x020A: // WM_MOUSEWHEEL
                {
                    int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    EmitMouse(lParam, 0x0800, delta);
                    break;
                }
                case 0x0100: // WM_KEYDOWN
                case 0x0104: // WM_SYSKEYDOWN
                case 0x0101: // WM_KEYUP
                case 0x0105: // WM_SYSKEYUP
                {
                    bool isUp = msg == 0x0101 || msg == 0x0105;
                    int vk = wParam.ToInt32();
                    if (!isUp && vk == 0x1B && (GetKeyState(0x11) < 0) && (GetKeyState(0x12) < 0))
                    {
                        ControlEscapePressed?.Invoke();
                        return IntPtr.Zero;
                    }
                    long lp = lParam.ToInt64();
                    ushort scan = (ushort)((lp >> 16) & 0xFF);
                    bool ext = ((lp >> 24) & 1) != 0;
                    KeyEvent?.Invoke(scan, ext, isUp);
                    return IntPtr.Zero;
                }
            }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void EmitMouse(IntPtr lParam, uint flags, int data)
    {
        long lp = lParam.ToInt64();
        int px = (short)(lp & 0xFFFF);
        int py = (short)((lp >> 16) & 0xFFFF);
        var (vx, vy, vw, vh) = ComputeLetterboxRect();
        double nx = Math.Clamp((px - vx) / Math.Max(1f, vw), 0.0, 1.0);
        double ny = Math.Clamp((py - vy) / Math.Max(1f, vh), 0.0, 1.0);
        MouseEvent?.Invoke(nx, ny, flags, data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
            _uiThread.Join(1000);
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(int dwEx, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? mod);

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASSEXW { public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
}
