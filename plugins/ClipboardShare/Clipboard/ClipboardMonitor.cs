using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipboardShare.Clipboard;

/// <summary>从系统剪贴板读到的原始内容：文字，或图片（优先 PNG 格式，否则为 CF_DIB）。</summary>
public sealed record ClipboardContent(string? Text, byte[]? Png, byte[]? Dib);

/// <summary>
/// 系统剪贴板监听与读写。在独立 STA 线程上创建隐藏的消息窗口：
/// 用 AddClipboardFormatListener 即时收到变化通知，并每 250ms 检查序列号兜底（以管理员身份运行时部分通知可能被系统过滤）。
/// 本插件写入的内容会被识别并忽略，不会再次触发同步。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint WM_APP_WORK = 0x8001;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint MSGFLT_ALLOW = 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const nuint PollTimerId = 1;
    private const nuint ReadTimerId = 2;
    private const uint PollIntervalMs = 250;
    /// <summary>收到变化通知后稍等再读，部分程序会分多次写入不同格式。</summary>
    private const uint ReadDelayMs = 60;
    private const int MaxReadAttempts = 10;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly WndProc _wndProc;
    private Thread? _thread;
    private IntPtr _hwnd;
    private uint _lastSeq;
    private bool _readScheduled;
    private int _readAttempts;
    private bool _disposed;

    private readonly uint _fmtPng = RegisterClipboardFormatW("PNG");
    private readonly uint _fmtMarker = RegisterClipboardFormatW("EasyTierExt.ClipboardShare");
    private readonly uint _fmtExclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _fmtViewerIgnore = RegisterClipboardFormatW("Clipboard Viewer Ignore");
    private readonly uint _fmtCanIncludeHistory = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private readonly uint _fmtCanUploadCloud = RegisterClipboardFormatW("CanUploadToCloudClipboard");

    public ClipboardMonitor()
    {
        _wndProc = WindowProc;
    }

    /// <summary>用户复制了新内容。在线程池上触发。</summary>
    public event Action<ClipboardContent>? Copied;

    /// <summary>复制的内容不会同步（如文件、密码管理器标记为敏感的内容）。参数为原因。</summary>
    public event Action<string>? Skipped;

    public bool IsRunning => _hwnd != IntPtr.Zero;

    public void Start()
    {
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "ClipboardMonitor" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public Task WriteTextAsync(string text) => RunOnThreadAsync(() =>
    {
        WithClipboard(() =>
        {
            SetData(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
        });
    });

    /// <summary>写入图片：同时放入 PNG（保留透明度）与 CF_DIB（兼容所有程序）。</summary>
    public Task WriteImageAsync(byte[] png, byte[] dib) => RunOnThreadAsync(() =>
    {
        WithClipboard(() =>
        {
            SetData(CF_DIB, dib);
            if (_fmtPng != 0) SetData(_fmtPng, png);
        });
    });

    private void WithClipboard(Action setFormats)
    {
        if (!OpenClipboardWithRetry()) throw new IOException("剪贴板被其他程序占用");
        try
        {
            if (!EmptyClipboard()) throw new IOException("无法清空剪贴板");
            setFormats();
            if (_fmtMarker != 0) SetData(_fmtMarker, [1, 0, 0, 0]);
        }
        finally
        {
            CloseClipboard();
        }
        // 记下写入后的序列号，变化通知到来时据此识别为本插件写入
        _lastSeq = GetClipboardSequenceNumber();
    }

    private static void SetData(uint format, byte[] data)
    {
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        if (h == IntPtr.Zero) throw new OutOfMemoryException();
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            GlobalFree(h);
            throw new OutOfMemoryException();
        }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == IntPtr.Zero)
        {
            GlobalFree(h);
            throw new IOException("写入剪贴板失败");
        }
        // 成功后内存归系统所有，不能再释放
    }

    private Task RunOnThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_hwnd == IntPtr.Zero)
        {
            tcs.SetException(new InvalidOperationException("剪贴板监听未启动"));
            return tcs.Task;
        }
        _work.Enqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        PostMessageW(_hwnd, WM_APP_WORK, 0, 0);
        return tcs.Task;
    }

    private void ThreadMain()
    {
        string className = $"EasyTierClipboardShare_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = className
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            _ready.Set();
            return;
        }

        _hwnd = CreateWindowExW(0, className, "EasyTier Clipboard Share", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _ready.Set();
            return;
        }

        // 允许较低权限的进程触发变化通知（宿主以管理员身份运行）
        ChangeWindowMessageFilterEx(_hwnd, WM_CLIPBOARDUPDATE, MSGFLT_ALLOW, IntPtr.Zero);
        AddClipboardFormatListener(_hwnd);
        // 插件启动前已在剪贴板中的内容不算新的复制
        _lastSeq = GetClipboardSequenceNumber();
        SetTimer(_hwnd, PollTimerId, PollIntervalMs, IntPtr.Zero);
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        RemoveClipboardFormatListener(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                ScheduleRead();
                return IntPtr.Zero;
            case WM_TIMER when (nuint)wParam == PollTimerId:
                if (!_readScheduled && GetClipboardSequenceNumber() != _lastSeq) ScheduleRead();
                return IntPtr.Zero;
            case WM_TIMER when (nuint)wParam == ReadTimerId:
                KillTimer(hwnd, ReadTimerId);
                _readScheduled = false;
                ReadNow();
                return IntPtr.Zero;
            case WM_APP_WORK:
                while (_work.TryDequeue(out var action)) action();
                return IntPtr.Zero;
            case WM_DESTROY:
                KillTimer(hwnd, PollTimerId);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ScheduleRead()
    {
        if (_readScheduled) return;
        _readScheduled = true;
        SetTimer(_hwnd, ReadTimerId, ReadDelayMs, IntPtr.Zero);
    }

    private void ReadNow()
    {
        uint seq = GetClipboardSequenceNumber();
        if (seq == _lastSeq) return;

        if (!OpenClipboard(_hwnd))
        {
            // 其他程序正占用剪贴板，稍后重试
            if (++_readAttempts < MaxReadAttempts)
            {
                ScheduleRead();
            }
            else
            {
                _readAttempts = 0;
                _lastSeq = seq;
            }
            return;
        }

        _readAttempts = 0;
        ClipboardContent? content = null;
        string? skipped = null;
        try
        {
            _lastSeq = GetClipboardSequenceNumber();
            (content, skipped) = ReadOpenClipboard();
        }
        catch
        {
            content = null;
        }
        finally
        {
            CloseClipboard();
        }

        if (content is not null)
        {
            var c = content;
            ThreadPool.QueueUserWorkItem(_ => Copied?.Invoke(c));
        }
        else if (skipped is not null)
        {
            var reason = skipped;
            ThreadPool.QueueUserWorkItem(_ => Skipped?.Invoke(reason));
        }
    }

    /// <summary>读取已打开的剪贴板：返回可同步的内容，或不同步的原因（两者都为 null 表示忽略）。</summary>
    private (ClipboardContent? Content, string? Skipped) ReadOpenClipboard()
    {
        if (IsAvailable(_fmtMarker)) return (null, null); // 本插件写入的内容
        if (IsAvailable(_fmtExclude) || IsAvailable(_fmtViewerIgnore) ||
            ReadDword(_fmtCanIncludeHistory) == 0 || ReadDword(_fmtCanUploadCloud) == 0)
        {
            return (null, "复制的内容被来源程序标记为敏感（如密码），未记录也未同步");
        }

        if (IsClipboardFormatAvailable(CF_UNICODETEXT) && ReadBytes(CF_UNICODETEXT) is { } textBytes)
        {
            string text = Encoding.Unicode.GetString(textBytes);
            int nul = text.IndexOf('\0');
            if (nul >= 0) text = text[..nul];
            return (text.Length > 0 ? new ClipboardContent(text, null, null) : null, null);
        }
        if (IsAvailable(_fmtPng) && ReadBytes(_fmtPng) is { Length: > 8 } png)
        {
            return (new ClipboardContent(null, png, null), null);
        }
        if (IsClipboardFormatAvailable(CF_DIB) && ReadBytes(CF_DIB) is { Length: > 40 } dib)
        {
            return (new ClipboardContent(null, null, dib), null);
        }
        if (IsClipboardFormatAvailable(CF_HDROP))
        {
            return (null, "暂不支持同步文件，仅同步文字与图片");
        }
        return (null, null);
    }

    private bool IsAvailable(uint format) => format != 0 && IsClipboardFormatAvailable(format);

    private uint? ReadDword(uint format)
    {
        if (!IsAvailable(format)) return null;
        var bytes = ReadBytes(format);
        return bytes is { Length: >= 4 } ? BitConverter.ToUInt32(bytes, 0) : null;
    }

    private static byte[]? ReadBytes(uint format)
    {
        IntPtr h = GetClipboardData(format);
        if (h == IntPtr.Zero) return null;
        nuint size = GlobalSize(h);
        if (size == 0 || size > (nuint)Engine.ClipProtocol.MaxFrameBytes) return null;
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var data = new byte[(int)size];
            Marshal.Copy(p, data, 0, data.Length);
            return data;
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    private bool OpenClipboardWithRetry()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(_hwnd)) return true;
            Thread.Sleep(30);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            var hwnd = _hwnd;
            _work.Enqueue(() => DestroyWindow(hwnd));
            PostMessageW(hwnd, WM_APP_WORK, 0, 0);
            _thread?.Join(TimeSpan.FromMilliseconds(500));
        }
        _ready.Dispose();
    }

    // ---------------- Win32 ----------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(IntPtr hwnd, nuint id, uint elapse, IntPtr timerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, nuint id);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr mem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr mem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr mem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr mem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr mem);
}
