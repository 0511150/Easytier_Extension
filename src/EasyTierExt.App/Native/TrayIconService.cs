using System.Runtime.InteropServices;

namespace EasyTierExt.App.Native;

public sealed class TrayIconService : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int CMD_SHOW = 1001;
    private const int CMD_EXIT = 1002;
    private const int CMD_AUTOSTART = 1003;

    private readonly Action _onShow;
    private readonly Func<bool> _isAutoStartEnabled;
    private readonly Action _onToggleAutoStart;
    private readonly Action _onExit;
    private readonly WndProcDelegate _wndProcDelegate;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconService(Action onShow, Func<bool> isAutoStartEnabled, Action onToggleAutoStart, Action onExit)
    {
        _onShow = onShow;
        _isAutoStartEnabled = isAutoStartEnabled;
        _onToggleAutoStart = onToggleAutoStart;
        _onExit = onExit;
        _wndProcDelegate = WndProc;
        CreateHelperWindowAndIcon();
    }

    private unsafe void CreateHelperWindowAndIcon()
    {
        var hInst = Win32Helpers.GetModuleHandleW(null);
        var className = $"EasyTierExtTray_{Environment.ProcessId}";
        var wc = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInst,
            lpszClassName = className
        };
        RegisterClassExW(ref wc);

        _hwnd = Win32Helpers.CreateWindowExW(
            0, className, "EasyTierExtTrayHost", 0,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        _hIcon = CreateProgrammaticIcon();
        if (_hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = "EasyTier with Extension"
            };
            _iconAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                _onShow();
                return IntPtr.Zero;
            }
            if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(hMenu, MF_STRING, CMD_SHOW, "打开主窗口");
            AppendMenuW(hMenu, MF_STRING | (_isAutoStartEnabled() ? MF_CHECKED : 0), CMD_AUTOSTART, "开机自启动");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, CMD_EXIT, "彻底退出");

            GetCursorPos(out var pt);
            Win32Helpers.SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            if (cmd == CMD_SHOW)
            {
                _onShow();
            }
            else if (cmd == CMD_AUTOSTART)
            {
                _onToggleAutoStart();
            }
            else if (cmd == CMD_EXIT)
            {
                _onExit();
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private static unsafe IntPtr CreateProgrammaticIcon()
    {
        const int size = 32;
        uint* pixels = stackalloc uint[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int dx = x - 16;
                int dy = y - 16;
                int distSq = dx * dx + dy * dy;
                if (distSq <= 14 * 14)
                {
                    bool innerRing = distSq >= 7 * 7 && distSq <= 10 * 10;
                    pixels[y * size + x] = innerRing ? 0xFFF3F4F6u : 0xFF7C5CFFu;
                }
                else
                {
                    pixels[y * size + x] = 0x00000000u;
                }
            }
        }

        IntPtr hColor = CreateBitmap(size, size, 1, 32, (IntPtr)pixels);
        IntPtr hMask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var ii = new ICONINFO
        {
            fIcon = true,
            hbmMask = hMask,
            hbmColor = hColor
        };
        IntPtr icon = CreateIconIndirect(ref ii);
        if (hColor != IntPtr.Zero) DeleteObject(hColor);
        if (hMask != IntPtr.Zero) DeleteObject(hMask);
        return icon;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_iconAdded && _hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIconW(NIM_DELETE, ref nid);
            _iconAdded = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            Win32Helpers.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }
}
