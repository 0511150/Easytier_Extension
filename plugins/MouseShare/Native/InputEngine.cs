using System.Collections.Concurrent;
using MouseShare.Engine;

namespace MouseShare.Native;

public sealed record EngineState(bool Remote, string? TargetDevice, string? ControlledBy, string? ActiveDevice, string? ActiveMonitor);

/// <summary>
/// 键鼠引擎：在独立的消息线程上安装低级键盘/鼠标钩子。
/// <list type="bullet">
/// <item>本机光标碰到与其他设备相接的边时切换为“控制对方”：本机光标停在离开时所在显示器的中心并隐藏，
/// 钩子吞掉本机键鼠事件，换算成对方桌面的绝对坐标发送过去；碰到通往本机的边时恢复。</item>
/// <item>被其他设备控制时注入对方发来的键鼠；本机鼠标被明显移动或点击时收回控制权。</item>
/// </list>
/// 钩子回调必须很快返回：只做坐标计算与入队发送，注入等操作投递到消息循环里执行。
/// </summary>
internal sealed class InputEngine : IDisposable
{
    private const uint WM_INVOKE = Win32.WM_APP + 1;
    private const int IdentifyTimerId = 1;
    private const int IdentifyMs = 2500;
    private const double TakeBackPixels = 12;
    private const string HiderClass = "EasyTierMouseShareHider";
    private const string OverlayClass = "EasyTierMouseShareIdentify";

    private readonly Func<string, byte[], bool> _send;
    private readonly Injector _injector = new();
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly Win32.WndProc _wndProc;
    private readonly Win32.HookProc _mouseProc;
    private readonly Win32.HookProc _keyProc;
    private readonly Dictionary<IntPtr, string> _overlays = [];

    private IntPtr _hInstance;
    private IntPtr _hwnd;
    private IntPtr _blankCursor;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;

    // 跨线程读写
    private volatile LayoutSnapshot? _snapshot;
    private volatile bool _enabled = true;
    private volatile bool _remoteFlag;
    private volatile string? _stateTarget;
    private volatile string? _controlledBy;
    private volatile string? _activeDevice;
    private volatile string? _activeMonitor;
    private volatile bool _resetTakeBack;

    // 仅钩子线程访问
    private bool _remote;
    private PlacedMonitor? _target;
    private double _vx, _vy;
    private int _lastSentX = int.MinValue, _lastSentY = int.MinValue;
    private PlacedMonitor? _parkMonitor;
    private Win32.POINT _park;
    private Win32.POINT _returnPoint;
    private int _remoteButtons;
    private int _localButtons;
    private readonly HashSet<(ushort Vk, ushort Scan, bool Ext)> _localKeys = [];
    private double _takeBackDistance;
    private long _lastPhysicalMove;

    public InputEngine(Func<string, byte[], bool> send)
    {
        _send = send;
        _wndProc = WndProc;
        _mouseProc = MouseHookProc;
        _keyProc = KeyHookProc;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "MouseShare.Input", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>控制状态、光标所在显示器变化时触发（线程池线程）。</summary>
    public event Action? StateChanged;

    public EngineState Current => new(_remoteFlag, _stateTarget, _controlledBy, _activeDevice, _activeMonitor);

    public bool HooksInstalled { get; private set; }

    public void UpdateSnapshot(LayoutSnapshot snapshot)
    {
        _snapshot = snapshot;
        Post(() =>
        {
            if (!_remote || _target is null || _parkMonitor is null) return;
            var t = snapshot.Find(_target.DeviceId, _target.MonitorId);
            var park = snapshot.Find(snapshot.LocalId, _parkMonitor.MonitorId);
            if (t is null || park is null)
            {
                ReturnLocal(_returnPoint.X, _returnPoint.Y);
                return;
            }
            _target = t;
            _parkMonitor = park;
        });
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled) return;
        Post(() =>
        {
            if (_remote) ReturnLocal(_returnPoint.X, _returnPoint.Y);
        });
        if (_controlledBy is { } c) ReleaseController(c, notify: true);
    }

    /// <summary>有已连接设备时才安装钩子，没有时卸载，避免无谓地经手全部键鼠输入。</summary>
    public void SetHooks(bool on) => Post(() =>
    {
        if (on && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, _hInstance, 0);
            _keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyProc, _hInstance, 0);
            HooksInstalled = _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero;
        }
        else if (!on && _mouseHook != IntPtr.Zero)
        {
            if (_remote) ReturnLocal(_returnPoint.X, _returnPoint.Y);
            RemoveHooks();
            _activeDevice = _activeMonitor = null;
            RaiseState();
        }
    });

    /// <summary>与某台设备的连接断开。</summary>
    public void PeerLost(string deviceId)
    {
        Post(() =>
        {
            if (_remote && _target?.DeviceId == deviceId) ReturnLocal(_returnPoint.X, _returnPoint.Y);
        });
        if (_controlledBy == deviceId) ReleaseController(deviceId, notify: false);
    }

    /// <summary>在本机各显示器中央短暂显示编号（显示器 id -> 编号）。</summary>
    public void ShowIdentify(IReadOnlyDictionary<string, string> numbers) => Post(() =>
    {
        CloseOverlays();
        var snap = _snapshot;
        if (snap is null) return;
        foreach (var m in snap.Local)
        {
            if (!numbers.TryGetValue(m.MonitorId, out var text)) continue;
            int size = Math.Max(120, (int)(Math.Min(m.Pixel.W, m.Pixel.H) * 0.3));
            int x = m.Pixel.X + (m.Pixel.W - size) / 2;
            int y = m.Pixel.Y + (m.Pixel.H - size) / 2;
            var h = Win32.CreateWindowEx(
                Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT,
                OverlayClass, string.Empty, Win32.WS_POPUP, x, y, size, size, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
            if (h == IntPtr.Zero) continue;
            Win32.SetLayeredWindowAttributes(h, 0, 235, Win32.LWA_ALPHA);
            int round = 2;
            Win32.DwmSetWindowAttribute(h, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            _overlays[h] = text;
            Win32.ShowWindow(h, Win32.SW_SHOWNOACTIVATE);
        }
        Win32.SetTimer(_hwnd, (UIntPtr)IdentifyTimerId, IdentifyMs, IntPtr.Zero);
    });

    // ---------------- 被控方：处理对方发来的键鼠 ----------------

    public void HandleRemote(string from, MsgType type, byte[] payload)
    {
        switch (type)
        {
            case MsgType.Enter:
            {
                // 本机正在控制别的设备或已停用时拒绝，让对方收回光标
                if (!_enabled || _remoteFlag)
                {
                    _send(from, Protocol.Frame(MsgType.TakeBack));
                    return;
                }
                var prev = _controlledBy;
                if (prev is not null && prev != from) _injector.ReleaseAll();
                _controlledBy = from;
                _resetTakeBack = true;
                var (x, y) = Protocol.ReadPoint(payload);
                _injector.MoveTo(x, y);
                RaiseState();
                break;
            }
            case MsgType.Leave:
                if (_controlledBy == from) ReleaseController(from, notify: false);
                break;
            case MsgType.MouseMove when _controlledBy == from:
            {
                var (x, y) = Protocol.ReadPoint(payload);
                _injector.MoveTo(x, y);
                break;
            }
            case MsgType.MouseButton when _controlledBy == from:
            {
                var (button, down) = Protocol.ReadButton(payload);
                _injector.Button(button, down);
                break;
            }
            case MsgType.MouseWheel when _controlledBy == from:
            {
                var (delta, horizontal) = Protocol.ReadWheel(payload);
                _injector.Wheel(delta, horizontal);
                break;
            }
            case MsgType.Key when _controlledBy == from:
            {
                var (vk, scan, ext, down) = Protocol.ReadKey(payload);
                _injector.Key(vk, scan, ext, down);
                break;
            }
            case MsgType.TakeBack:
                Post(() =>
                {
                    if (_remote && _target?.DeviceId == from) ReturnLocal(_returnPoint.X, _returnPoint.Y);
                });
                break;
        }
    }

    private void ReleaseController(string controller, bool notify)
    {
        if (_controlledBy != controller) return;
        _controlledBy = null;
        // 可能在钩子回调中调用：注入放到线程池，不在钩子线程上 SendInput
        ThreadPool.QueueUserWorkItem(_ => _injector.ReleaseAll());
        if (notify) _send(controller, Protocol.Frame(MsgType.TakeBack));
        RaiseState();
    }

    // ---------------- 钩子 ----------------

    private unsafe IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = *(Win32.MSLLHOOKSTRUCT*)lParam;
            if (info.dwExtraInfo != Injector.Signature)
            {
                bool swallow = false;
                try
                {
                    swallow = _remote ? OnRemoteMouse((int)wParam, info) : OnLocalMouse((int)wParam, info);
                }
                catch
                {
                    // 钩子里绝不能抛出异常
                }
                if (swallow) return 1;
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private unsafe IntPtr KeyHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = *(Win32.KBDLLHOOKSTRUCT*)lParam;
            if (info.dwExtraInfo != Injector.Signature)
            {
                int msg = (int)wParam;
                bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                var key = ((ushort)info.vkCode, (ushort)info.scanCode, (info.flags & Win32.LLKHF_EXTENDED) != 0);
                if (_remote && _target is { } t)
                {
                    if (!_send(t.DeviceId, Protocol.KeyFrame(key.Item1, key.Item2, key.Item3, down)))
                    {
                        Post(() => ReturnLocal(_returnPoint.X, _returnPoint.Y));
                    }
                    return 1;
                }
                if (down) _localKeys.Add(key);
                else _localKeys.Remove(key);
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static int ButtonOf(int msg, uint mouseData, out bool down)
    {
        down = msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN;
        return msg switch
        {
            Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP => 1,
            Win32.WM_RBUTTONDOWN or Win32.WM_RBUTTONUP => 2,
            Win32.WM_MBUTTONDOWN or Win32.WM_MBUTTONUP => 3,
            Win32.WM_XBUTTONDOWN or Win32.WM_XBUTTONUP => (mouseData >> 16) == 2 ? 5 : 4,
            _ => 0
        };
    }

    private bool OnLocalMouse(int msg, in Win32.MSLLHOOKSTRUCT info)
    {
        int button = ButtonOf(msg, info.mouseData, out bool down);
        if (button > 0)
        {
            if (down) _localButtons |= 1 << button;
            else _localButtons &= ~(1 << button);
        }

        // 正被其他设备控制：本机鼠标被点击或明显移动时收回控制权
        if (_controlledBy is { } controller)
        {
            if (_resetTakeBack)
            {
                _resetTakeBack = false;
                _takeBackDistance = 0;
            }
            if (button > 0 && down)
            {
                ReleaseController(controller, notify: true);
            }
            else if (msg == Win32.WM_MOUSEMOVE && Win32.GetCursorPos(out var cur))
            {
                long now = Environment.TickCount64;
                if (now - _lastPhysicalMove > 500) _takeBackDistance = 0;
                _lastPhysicalMove = now;
                _takeBackDistance += Math.Abs(info.pt.X - cur.X) + Math.Abs(info.pt.Y - cur.Y);
                if (_takeBackDistance > TakeBackPixels) ReleaseController(controller, notify: true);
            }
        }

        if (msg != Win32.WM_MOUSEMOVE) return false;
        var snap = _snapshot;
        var m = snap?.LocalAtOrNearest(info.pt.X, info.pt.Y);
        if (snap is null || m is null) return false;
        SetActive(m);
        if (!_enabled || _localButtons != 0 || _controlledBy is not null || !snap.HasRemote) return false;
        return TryLeaveLocal(snap, m, info.pt.X, info.pt.Y);
    }

    /// <summary>光标碰到本机桌面的外边缘且该处紧挨着其他设备时，切换为控制该设备。</summary>
    private bool TryLeaveLocal(LayoutSnapshot snap, PlacedMonitor m, int x, int y)
    {
        var p = m.Pixel;
        int cx = Math.Clamp(x, p.X, p.Right - 1), cy = Math.Clamp(y, p.Y, p.Bottom - 1);
        if (x <= p.X && !snap.LocalContains(p.X - 1, cy) && TryEnter(snap, m, Edge.Left, cy, (p.X + 1, cy))) return true;
        if (x >= p.Right - 1 && !snap.LocalContains(p.Right, cy) && TryEnter(snap, m, Edge.Right, cy, (p.Right - 2, cy))) return true;
        if (y <= p.Y && !snap.LocalContains(cx, p.Y - 1) && TryEnter(snap, m, Edge.Up, cx, (cx, p.Y + 1))) return true;
        if (y >= p.Bottom - 1 && !snap.LocalContains(cx, p.Bottom) && TryEnter(snap, m, Edge.Down, cx, (cx, p.Bottom - 2))) return true;
        return false;
    }

    private bool TryEnter(LayoutSnapshot snap, PlacedMonitor from, Edge edge, int pixelAlong, (int X, int Y) returnPoint)
    {
        double along = Geometry.AlongFromPixel(from, edge, pixelAlong);
        var to = Geometry.FindNeighbor(snap.All, from, edge, along);
        if (to is null || to.DeviceId == snap.LocalId) return false;

        var (ex, ey) = Geometry.EntryPixel(to, edge, along);
        if (!_send(to.DeviceId, Protocol.PointFrame(MsgType.Enter, ex, ey))) return false;

        _remote = true;
        _remoteFlag = true;
        _target = to;
        _stateTarget = to.DeviceId;
        _vx = ex + 0.5;
        _vy = ey + 0.5;
        _lastSentX = ex;
        _lastSentY = ey;
        _remoteButtons = 0;
        _parkMonitor = from;
        _park = new Win32.POINT { X = from.Pixel.X + from.Pixel.W / 2, Y = from.Pixel.Y + from.Pixel.H / 2 };
        _returnPoint = new Win32.POINT { X = returnPoint.X, Y = returnPoint.Y };

        Win32.SetCursorPos(_park.X, _park.Y);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _park.X, _park.Y, 1, 1, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        Win32.SetCursor(_blankCursor);
        // 本机仍按着的键在离开前松开，否则本机程序会一直认为它们处于按下状态
        ReleaseLocalKeys();
        SetActive(to);
        RaiseState();
        return true;
    }

    private bool OnRemoteMouse(int msg, in Win32.MSLLHOOKSTRUCT info)
    {
        var target = _target;
        if (target is null) return false;

        switch (msg)
        {
            case Win32.WM_MOUSEMOVE:
            {
                // 回调时系统尚未移动光标，当前位置即停放点；两者之差就是本次移动量（已含本机指针加速）
                if (!Win32.GetCursorPos(out var cur)) cur = _park;
                int dx = info.pt.X - cur.X, dy = info.pt.Y - cur.Y;
                if (cur.X != _park.X || cur.Y != _park.Y) Win32.SetCursorPos(_park.X, _park.Y);
                if (dx != 0 || dy != 0) MoveVirtual(dx, dy);
                return true;
            }
            case Win32.WM_MOUSEWHEEL:
            case Win32.WM_MOUSEHWHEEL:
                Send(target, Protocol.WheelFrame((short)(info.mouseData >> 16), msg == Win32.WM_MOUSEHWHEEL));
                return true;
            default:
            {
                int button = ButtonOf(msg, info.mouseData, out bool down);
                if (button > 0)
                {
                    if (down) _remoteButtons |= 1 << button;
                    else _remoteButtons &= ~(1 << button);
                    Send(target, Protocol.ButtonFrame(button, down));
                }
                return true;
            }
        }
    }

    /// <summary>按两台显示器的布局比例换算移动量，越过对方桌面边缘时切换到相邻设备或回到本机。</summary>
    private void MoveVirtual(int dx, int dy)
    {
        var snap = _snapshot;
        var t = _target;
        var park = _parkMonitor;
        if (snap is null || t is null || park is null)
        {
            ReturnLocal(_returnPoint.X, _returnPoint.Y);
            return;
        }

        double k = park.Factor / t.Factor;
        _vx += dx * k;
        _vy += dy * k;
        int ix = (int)Math.Floor(_vx), iy = (int)Math.Floor(_vy);

        if (!t.Pixel.Contains(ix, iy))
        {
            PlacedMonitor? inside = null;
            foreach (var m in snap.Of(t.DeviceId))
            {
                if (m.Pixel.Contains(ix, iy))
                {
                    inside = m;
                    break;
                }
            }

            if (inside is not null)
            {
                _target = t = inside;
                SetActive(inside);
            }
            else
            {
                var p = t.Pixel;
                double overL = p.X - _vx, overR = _vx - p.Right, overU = p.Y - _vy, overD = _vy - p.Bottom;
                Edge edge = Math.Max(overL, overR) >= Math.Max(overU, overD)
                    ? (overL > overR ? Edge.Left : Edge.Right)
                    : (overU > overD ? Edge.Up : Edge.Down);
                int alongPx = edge is Edge.Left or Edge.Right ? Math.Clamp(iy, p.Y, p.Bottom - 1) : Math.Clamp(ix, p.X, p.Right - 1);

                // 按住鼠标键时不跨屏（拖动无法跨设备）
                if (_remoteButtons == 0)
                {
                    double along = Geometry.AlongFromPixel(t, edge, alongPx);
                    var to = Geometry.FindNeighbor(snap.All, t, edge, along);
                    if (to is not null)
                    {
                        var (ex, ey) = Geometry.EntryPixel(to, edge, along);
                        if (to.DeviceId == snap.LocalId) ReturnLocal(ex, ey);
                        else SwitchTarget(to, ex, ey);
                        return;
                    }
                }

                _vx = Math.Clamp(_vx, p.X, p.Right - 0.001);
                _vy = Math.Clamp(_vy, p.Y, p.Bottom - 0.001);
                ix = (int)Math.Floor(_vx);
                iy = (int)Math.Floor(_vy);
            }
        }

        if (ix == _lastSentX && iy == _lastSentY) return;
        _lastSentX = ix;
        _lastSentY = iy;
        Send(t, Protocol.PointFrame(MsgType.MouseMove, ix, iy));
    }

    private void SwitchTarget(PlacedMonitor to, int ex, int ey)
    {
        var old = _target;
        if (old is not null) _send(old.DeviceId, Protocol.Frame(MsgType.Leave));
        if (!_send(to.DeviceId, Protocol.PointFrame(MsgType.Enter, ex, ey)))
        {
            ReturnLocal(_returnPoint.X, _returnPoint.Y);
            return;
        }
        _target = to;
        _stateTarget = to.DeviceId;
        _vx = ex + 0.5;
        _vy = ey + 0.5;
        _lastSentX = ex;
        _lastSentY = ey;
        _remoteButtons = 0;
        SetActive(to);
        RaiseState();
    }

    private void ReturnLocal(int x, int y)
    {
        if (!_remote) return;
        var old = _target;
        _remote = false;
        _remoteFlag = false;
        _target = null;
        _stateTarget = null;
        _remoteButtons = 0;
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        Win32.SetCursorPos(x, y);
        if (old is not null) _send(old.DeviceId, Protocol.Frame(MsgType.Leave));
        if (_snapshot?.LocalAtOrNearest(x, y) is { } m) SetActive(m);
        RaiseState();
    }

    private void Send(PlacedMonitor target, byte[] frame)
    {
        if (!_send(target.DeviceId, frame))
        {
            // 连接已断开：投递到消息循环恢复本机，不在钩子回调里改动窗口
            Post(() => ReturnLocal(_returnPoint.X, _returnPoint.Y));
        }
    }

    private void ReleaseLocalKeys()
    {
        if (_localKeys.Count == 0) return;
        var keys = _localKeys.ToArray();
        _localKeys.Clear();
        // 注入不在钩子线程上进行（钩子线程要及时处理注入事件本身经过的钩子回调）
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var k in keys) Injector.SendKey(k.Vk, k.Scan, k.Ext, down: false);
        });
    }

    private void SetActive(PlacedMonitor m)
    {
        if (m.DeviceId == _activeDevice && m.MonitorId == _activeMonitor) return;
        _activeDevice = m.DeviceId;
        _activeMonitor = m.MonitorId;
        RaiseState();
    }

    private void RaiseState()
    {
        var handler = StateChanged;
        if (handler is not null) ThreadPool.QueueUserWorkItem(_ => handler());
    }

    // ---------------- 消息线程 ----------------

    private void Post(Action action)
    {
        _queue.Enqueue(action);
        if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadMain()
    {
        _hInstance = Win32.GetModuleHandle(null);
        // 全透明光标：AND 掩码全 1、XOR 掩码全 0
        var and = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray();
        var xor = new byte[32 * 32 / 8];
        _blankCursor = Win32.CreateCursor(_hInstance, 0, 0, 32, 32, and, xor);

        RegisterClass(HiderClass, _blankCursor);
        RegisterClass(OverlayClass, IntPtr.Zero);

        // 1×1 的近乎透明置顶窗口：控制对方时放在停放点下方，借窗口类光标把本机光标隐藏
        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED,
            HiderClass, string.Empty, Win32.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Win32.SetLayeredWindowAttributes(_hwnd, 0, 1, Win32.LWA_ALPHA);
        _ready.Set();

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        RemoveHooks();
        CloseOverlays();
        if (_hwnd != IntPtr.Zero) Win32.DestroyWindow(_hwnd);
    }

    private void RegisterClass(string name, IntPtr cursor)
    {
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            hCursor = cursor,
            lpszClassName = name
        };
        Win32.RegisterClassEx(ref wc);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INVOKE:
                while (_queue.TryDequeue(out var action))
                {
                    try { action(); } catch { }
                }
                return IntPtr.Zero;
            case Win32.WM_SETCURSOR when hwnd == _hwnd:
                Win32.SetCursor(_blankCursor);
                return 1;
            case Win32.WM_MOUSEACTIVATE:
                return Win32.MA_NOACTIVATE;
            case Win32.WM_TIMER when hwnd == _hwnd && wParam == IdentifyTimerId:
                Win32.KillTimer(_hwnd, (UIntPtr)IdentifyTimerId);
                CloseOverlays();
                return IntPtr.Zero;
            case Win32.WM_PAINT when _overlays.TryGetValue(hwnd, out var text):
                PaintOverlay(hwnd, text);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void PaintOverlay(IntPtr hwnd, string text)
    {
        var hdc = Win32.BeginPaint(hwnd, out var ps);
        Win32.GetClientRect(hwnd, out var rc);

        var bg = Win32.CreateSolidBrush(Win32.Rgb(30, 30, 34));
        Win32.FillRect(hdc, ref rc, bg);
        Win32.DeleteObject(bg);

        // 蓝色描边
        var accent = Win32.CreateSolidBrush(Win32.Rgb(59, 130, 246));
        int t = Math.Max(3, rc.Right / 40);
        foreach (var edge in new[]
                 {
                     new Win32.RECT { Left = 0, Top = 0, Right = rc.Right, Bottom = t },
                     new Win32.RECT { Left = 0, Top = rc.Bottom - t, Right = rc.Right, Bottom = rc.Bottom },
                     new Win32.RECT { Left = 0, Top = 0, Right = t, Bottom = rc.Bottom },
                     new Win32.RECT { Left = rc.Right - t, Top = 0, Right = rc.Right, Bottom = rc.Bottom }
                 })
        {
            var r = edge;
            Win32.FillRect(hdc, ref r, accent);
        }
        Win32.DeleteObject(accent);

        var font = Win32.CreateFont(-(int)(rc.Bottom * 0.6), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var old = Win32.SelectObject(hdc, font);
        Win32.SetBkMode(hdc, Win32.TRANSPARENT);
        Win32.SetTextColor(hdc, Win32.Rgb(255, 255, 255));
        Win32.DrawText(hdc, text, -1, ref rc, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        Win32.SelectObject(hdc, old);
        Win32.DeleteObject(font);
        Win32.EndPaint(hwnd, ref ps);
    }

    private void CloseOverlays()
    {
        foreach (var h in _overlays.Keys) Win32.DestroyWindow(h);
        _overlays.Clear();
    }

    private void RemoveHooks()
    {
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
        HooksInstalled = false;
    }

    public void Dispose()
    {
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            if (_remote) ReturnLocal(_returnPoint.X, _returnPoint.Y);
            RemoveHooks();
            Win32.PostQuitMessage(0);
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(1));
        _injector.ReleaseAll();
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
