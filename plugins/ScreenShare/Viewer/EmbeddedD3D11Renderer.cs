using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenShare.Viewer;

public sealed class EmbeddedD3D11Renderer : IDisposable
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
Texture2D tex0 : register(t0);
SamplerState sam0 : register(s0);
float4 PSMain(VSOut i) : SV_TARGET {
    return tex0.Sample(sam0, i.uv);
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
    private ID3D11Texture2D? _videoTex;
    private ID3D11ShaderResourceView? _videoSrv;
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

    public unsafe void PresentBgraFrame(byte[] bgraData, int width, int height)
    {
        if (_disposed || _device is null || _context is null || _swapChain is null || _rtv is null) return;

        lock (_renderLock)
        {
            if (_disposed || _device is null || _context is null || _swapChain is null || _rtv is null) return;

            if (_videoTex is null || _videoW != width || _videoH != height)
            {
                _videoSrv?.Dispose();
                _videoTex?.Dispose();
                _videoW = width;
                _videoH = height;
                _videoTex = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write
                });
                _videoSrv = _device.CreateShaderResourceView(_videoTex);
            }

            var mapped = _context.Map(_videoTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int srcRowBytes = width * 4;
                fixed (byte* pSrc = bgraData)
                {
                    byte* dst = (byte*)mapped.DataPointer;
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(pSrc + y * srcRowBytes, dst + y * (int)mapped.RowPitch, srcRowBytes, srcRowBytes);
                    }
                }
            }
            finally
            {
                _context.Unmap(_videoTex, 0);
            }

            _hasVideoFrame = true;
            _context.OMSetRenderTargets(_rtv);
            _context.ClearRenderTargetView(_rtv, new Color4(0.05f, 0.05f, 0.06f, 1f));

            var (vx, vy, vw, vh) = ComputeLetterboxRect();
            _context.RSSetViewport(new Viewport(vx, vy, vw, vh, 0f, 1f));
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            if (_videoSrv is not null)
            {
                _context.PSSetShaderResource(0, _videoSrv);
            }
            _context.PSSetSampler(0, _sampler);
            _context.Draw(4, 0);
            _swapChain.Present(0, PresentFlags.None);
        }
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
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0],
            out _device).CheckError();
        _context = _device!.ImmediateContext;

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

            if (vis && _context is not null && _rtv is not null && _swapChain is not null)
            {
                if (_hasVideoFrame && _videoSrv is not null)
                {
                    _context.OMSetRenderTargets(_rtv);
                    _context.ClearRenderTargetView(_rtv, new Color4(0.05f, 0.05f, 0.06f, 1f));
                    var (vx, vy, vw, vh) = ComputeLetterboxRect();
                    _context.RSSetViewport(new Viewport(vx, vy, vw, vh, 0f, 1f));
                    _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                    _context.VSSetShader(_vs);
                    _context.PSSetShader(_ps);
                    _context.PSSetShaderResource(0, _videoSrv);
                    _context.PSSetSampler(0, _sampler);
                    _context.Draw(4, 0);
                    _swapChain.Present(0, PresentFlags.None);
                }
                else
                {
                    _context.OMSetRenderTargets(_rtv);
                    _context.ClearRenderTargetView(_rtv, new Color4(0.05f, 0.05f, 0.06f, 1f));
                    _swapChain.Present(0, PresentFlags.None);
                }
            }
        }
    }

    private void CleanupOnUiThread()
    {
        lock (_renderLock)
        {
            _videoSrv?.Dispose();
            _videoSrv = null;
            _videoTex?.Dispose();
            _videoTex = null;
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
