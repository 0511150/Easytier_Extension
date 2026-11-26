namespace MouseShare.Native;

/// <summary>
/// 键鼠注入（被控方）。所有注入事件带 <see cref="Signature"/>，本机钩子据此忽略自己注入的输入。
/// 记录已按下的键与鼠标键，控制方离开或断线时全部松开，避免卡键。
/// </summary>
internal sealed class Injector
{
    public static readonly IntPtr Signature = new(0x4D534852); // "MSHR"

    private static readonly int InputSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>();

    private readonly object _lock = new();
    private readonly HashSet<(ushort Vk, ushort Scan, bool Ext)> _keys = [];
    private readonly HashSet<int> _buttons = [];

    public void MoveTo(int x, int y)
    {
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Math.Max(Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN), 1);
        int vh = Math.Max(Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN), 1);
        // 归一化到像素中心，避免取整误差落到相邻像素
        int nx = (int)Math.Clamp((x - vx + 0.5) * 65536.0 / vw, 0, 65535);
        int ny = (int)Math.Clamp((y - vy + 0.5) * 65536.0 / vh, 0, 65535);
        SendMouse(nx, ny, 0, Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK);
    }

    public void Button(int button, bool down)
    {
        uint flags;
        uint data = 0;
        switch (button)
        {
            case 1: flags = down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP; break;
            case 2: flags = down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP; break;
            case 3: flags = down ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP; break;
            case 4 or 5:
                flags = down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP;
                data = (uint)(button - 3); // XBUTTON1 = 1, XBUTTON2 = 2
                break;
            default: return;
        }
        lock (_lock)
        {
            if (down) _buttons.Add(button);
            else _buttons.Remove(button);
        }
        SendMouse(0, 0, data, flags);
    }

    public void Wheel(int delta, bool horizontal) =>
        SendMouse(0, 0, unchecked((uint)delta), horizontal ? Win32.MOUSEEVENTF_HWHEEL : Win32.MOUSEEVENTF_WHEEL);

    public void Key(ushort vk, ushort scan, bool extended, bool down)
    {
        lock (_lock)
        {
            if (down) _keys.Add((vk, scan, extended));
            else _keys.Remove((vk, scan, extended));
        }
        SendKey(vk, scan, extended, down);
    }

    /// <summary>松开所有由本机注入且尚未松开的键与鼠标键。</summary>
    public void ReleaseAll()
    {
        List<(ushort Vk, ushort Scan, bool Ext)> keys;
        List<int> buttons;
        lock (_lock)
        {
            keys = [.. _keys];
            buttons = [.. _buttons];
            _keys.Clear();
            _buttons.Clear();
        }
        foreach (var k in keys) SendKey(k.Vk, k.Scan, k.Ext, down: false);
        foreach (var b in buttons) Button(b, down: false);
    }

    /// <summary>
    /// 有扫描码的键按扫描码注入（由被控方自己的键盘布局翻译，等同在被控方接了一块键盘）；
    /// Pause 的扫描码与 NumLock 相同，只能按虚拟键码注入。
    /// </summary>
    public static void SendKey(ushort vk, ushort scan, bool extended, bool down)
    {
        uint flags = 0;
        if (scan != 0 && vk != Win32.VK_PAUSE) flags |= Win32.KEYEVENTF_SCANCODE;
        if (extended) flags |= Win32.KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= Win32.KEYEVENTF_KEYUP;
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            u = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Signature }
            }
        };
        Win32.SendInput(1, [input], InputSize);
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            u = new Win32.InputUnion
            {
                mi = new Win32.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Signature }
            }
        };
        Win32.SendInput(1, [input], InputSize);
    }
}
