using System.Runtime.InteropServices;

namespace EasyTierExt.App.Native;

internal static class Win32Helpers
{
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_EX_NOPARENTNOTIFY = 0x00000004;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;

    public const int OFN_FILEMUSTEXIST = 0x00001000;
    public const int OFN_PATHMUSTEXIST = 0x00000800;
    public const int OFN_NOCHANGEDIR = 0x00000008;

    public static string? OpenZipFileDialog(IntPtr ownerHwnd)
    {
        var fileBuffer = new char[1024];
        var ofn = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            hwndOwner = ownerHwnd,
            lpstrFilter = "插件压缩包 (*.zip)\0*.zip\0所有文件 (*.*)\0*.*\0\0",
            lpstrFile = new string('\0', fileBuffer.Length),
            nMaxFile = fileBuffer.Length,
            lpstrTitle = "选择插件压缩包 (.zip)",
            Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
        };

        if (GetOpenFileNameW(ref ofn))
        {
            return ofn.lpstrFile.TrimEnd('\0');
        }
        return null;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME lpofn);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
}
