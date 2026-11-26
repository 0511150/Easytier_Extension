using MouseShare.Engine;

namespace MouseShare.Native;

internal static class Monitors
{
    /// <summary>枚举本机显示器（物理像素坐标，需进程为 Per-Monitor V2 DPI 感知）。</summary>
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>(), szDevice = string.Empty };
            if (!Win32.GetMonitorInfo(hMon, ref info)) return true;

            double scale = 1;
            if (Win32.GetDpiForMonitor(hMon, Win32.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                scale = dpiX / 96.0;
            }

            var r = info.rcMonitor;
            list.Add(new MonitorInfo
            {
                Id = string.IsNullOrEmpty(info.szDevice) ? $"M{list.Count + 1}" : info.szDevice,
                X = r.Left,
                Y = r.Top,
                W = r.Right - r.Left,
                H = r.Bottom - r.Top,
                Scale = Math.Round(scale, 2),
                Primary = (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0
            });
            return true;
        }, IntPtr.Zero);

        return list.Where(m => m.W > 0 && m.H > 0)
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.X)
            .ThenBy(m => m.Y)
            .ToList();
    }
}
