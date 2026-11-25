using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenShare.Capture;

public sealed record CapturableWindowInfo(long Hwnd, string Title, string ProcessName, string DisplayLabel, bool IsSelfProcess = false);

public sealed record CapturableMonitorInfo(
    long HMonitor,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary,
    string DisplayLabel);

public static class WindowEnumerator
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;

    public static List<CapturableWindowInfo> GetCapturableWindows()
    {
        var result = new List<CapturableWindowInfo>();
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            long exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            {
                return true;
            }

            int len = GetWindowTextLengthW(hwnd);
            if (len <= 0)
            {
                return true;
            }

            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title) || string.Equals(title, "Program Manager", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string procName = "App";
            bool isSelf = false;
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                isSelf = pid == (uint)Environment.ProcessId;
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    if (!string.IsNullOrWhiteSpace(proc.ProcessName))
                    {
                        procName = proc.ProcessName;
                        if (string.Equals(procName, "EasyTierClient", StringComparison.OrdinalIgnoreCase))
                        {
                            isSelf = true;
                        }
                    }
                }
                catch
                {
                }
            }

            string shortTitle = title.Length > 44 ? $"{title[..44]}..." : title;
            string label = $"[{procName}] {shortTitle}";
            result.Add(new CapturableWindowInfo(hwnd.ToInt64(), title, procName, label, isSelf));
            return true;
        }, IntPtr.Zero);

        return result
            .OrderBy(w => w.IsSelfProcess ? 1 : 0)
            .ToList();
    }

    public static List<CapturableMonitorInfo> GetCapturableMonitors()
    {
        var raw = new List<(long HMon, string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(hMon, ref mi))
            {
                int w = Math.Max(1, mi.rcMonitor.Right - mi.rcMonitor.Left);
                int h = Math.Max(1, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
                bool primary = (mi.dwFlags & 1) != 0;
                raw.Add((hMon.ToInt64(), mi.szDevice ?? string.Empty, mi.rcMonitor.Left, mi.rcMonitor.Top, w, h, primary));
            }
            return true;
        }, IntPtr.Zero);

        var sorted = raw
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Left)
            .ThenBy(m => m.Top)
            .ToList();

        var result = new List<CapturableMonitorInfo>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            var m = sorted[i];
            string label = m.IsPrimary
                ? $"屏幕 {i + 1} · 主屏 ({m.Width}×{m.Height})"
                : $"屏幕 {i + 1} ({m.Width}×{m.Height})";
            result.Add(new CapturableMonitorInfo(m.HMon, m.DeviceName, m.Left, m.Top, m.Width, m.Height, m.IsPrimary, label));
        }

        return result;
    }

    public static bool IsValidMonitor(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero) return false;
        var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
        return GetMonitorInfoW(hMonitor, ref mi);
    }

    public static bool TryGetMonitorBounds(IntPtr hMonitor, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (hMonitor == IntPtr.Zero) return false;
        var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
        if (!GetMonitorInfoW(hMonitor, ref mi)) return false;

        left = mi.rcMonitor.Left;
        top = mi.rcMonitor.Top;
        width = Math.Max(1, mi.rcMonitor.Right - mi.rcMonitor.Left);
        height = Math.Max(1, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
        return true;
    }

    public static bool IsValidWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool TryGetWindowCaptureBounds(IntPtr hwnd, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (!IsValidWindow(hwnd))
        {
            return false;
        }

        if (DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>()) != 0 &&
            !GetWindowRect(hwnd, out rect))
        {
            return false;
        }

        left = rect.Left;
        top = rect.Top;
        width = Math.Max(1, rect.Right - rect.Left);
        height = Math.Max(1, rect.Bottom - rect.Top);
        return true;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
