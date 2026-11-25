using System.Collections.Concurrent;
using EasyTierExt.Contracts.Ipc;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTierExt.App.Native;

public sealed class ViewportHostController : IDisposable
{
    private readonly IntPtr _parentHwnd;
    private readonly FrameworkElement _rootElement;
    private readonly Func<string, EventViewportChangedParams, Task> _notifyPlugin;
    private readonly ConcurrentDictionary<string, SlotInfo> _slots = new();

    private string? _activePluginId;
    private string? _activeMenuId;
    private bool _disposed;

    public ViewportHostController(
        IntPtr parentHwnd,
        FrameworkElement rootElement,
        Func<string, EventViewportChangedParams, Task> notifyPlugin)
    {
        _parentHwnd = parentHwnd;
        _rootElement = rootElement;
        _notifyPlugin = notifyPlugin;
        _rootElement.SizeChanged += (_, _) =>
        {
            if (_activePluginId is not null && _activeMenuId is not null)
            {
                UpdatePosition(_activePluginId, _activeMenuId);
            }
        };
    }

    public UiGetViewportHostResult GetOrCreateHost(string pluginId, string menuId)
    {
        var key = $"{pluginId}|{menuId}";
        var slot = _slots.GetOrAdd(key, _ =>
        {
            var hwnd = Win32Helpers.CreateWindowExW(
                Win32Helpers.WS_EX_NOPARENTNOTIFY,
                "STATIC",
                "",
                Win32Helpers.WS_CHILD | Win32Helpers.WS_CLIPSIBLINGS | Win32Helpers.WS_CLIPCHILDREN,
                0, 0, 1, 1,
                _parentHwnd,
                IntPtr.Zero,
                Win32Helpers.GetModuleHandleW(null),
                IntPtr.Zero);
            return new SlotInfo(pluginId, menuId, hwnd);
        });

        return new UiGetViewportHostResult
        {
            Hwnd = slot.Hwnd.ToInt64(),
            Width = Math.Max(1, slot.Width),
            Height = Math.Max(1, slot.Height)
        };
    }

    public void AttachElement(string pluginId, string menuId, FrameworkElement element)
    {
        _activePluginId = pluginId;
        _activeMenuId = menuId;

        var info = GetOrCreateHost(pluginId, menuId);
        var key = $"{pluginId}|{menuId}";
        if (_slots.TryGetValue(key, out var slot))
        {
            slot.Element = element;
        }

        element.SizeChanged += (_, _) => UpdatePosition(pluginId, menuId);
        element.Loaded += (_, _) => UpdatePosition(pluginId, menuId);
        HideAllExcept(key);
        UpdatePosition(pluginId, menuId);
    }

    public void HideAll()
    {
        _activePluginId = null;
        _activeMenuId = null;
        HideAllExcept(null);
    }

    private void HideAllExcept(string? activeKey)
    {
        foreach (var (key, slot) in _slots)
        {
            if (key == activeKey)
            {
                continue;
            }
            if (slot.Visible)
            {
                slot.Visible = false;
                Win32Helpers.ShowWindowAsync(slot.Hwnd, Win32Helpers.SW_HIDE);
                _ = _notifyPlugin(slot.PluginId, new EventViewportChangedParams
                {
                    MenuId = slot.MenuId,
                    Visible = false,
                    Width = slot.Width,
                    Height = slot.Height
                });
            }
        }
    }

    public void UpdatePosition(string pluginId, string menuId)
    {
        if (_disposed) return;
        var key = $"{pluginId}|{menuId}";
        if (!_slots.TryGetValue(key, out var slot) || slot.Element is null || slot.Element.XamlRoot is null)
        {
            return;
        }

        if (_activePluginId != pluginId || _activeMenuId != menuId)
        {
            return;
        }

        double scale = slot.Element.XamlRoot.RasterizationScale;
        var transform = slot.Element.TransformToVisual(_rootElement);
        var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

        int x = (int)Math.Round(topLeft.X * scale);
        int y = (int)Math.Round(topLeft.Y * scale);
        int w = Math.Max(1, (int)Math.Round(slot.Element.ActualWidth * scale));
        int h = Math.Max(1, (int)Math.Round(slot.Element.ActualHeight * scale));

        bool changed = !slot.Visible || slot.Width != w || slot.Height != h || slot.X != x || slot.Y != y;
        slot.X = x;
        slot.Y = y;
        slot.Width = w;
        slot.Height = h;
        slot.Visible = true;

        Win32Helpers.SetWindowPos(
            slot.Hwnd,
            IntPtr.Zero,
            x, y, w, h,
            Win32Helpers.SWP_NOZORDER | Win32Helpers.SWP_NOACTIVATE | Win32Helpers.SWP_ASYNCWINDOWPOS);
        Win32Helpers.ShowWindowAsync(slot.Hwnd, Win32Helpers.SW_SHOW);

        if (changed)
        {
            _ = _notifyPlugin(pluginId, new EventViewportChangedParams
            {
                MenuId = menuId,
                Visible = true,
                Width = w,
                Height = h
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots.Values)
        {
            if (slot.Hwnd != IntPtr.Zero)
            {
                Win32Helpers.DestroyWindow(slot.Hwnd);
            }
        }
        _slots.Clear();
    }

    private sealed class SlotInfo(string pluginId, string menuId, IntPtr hwnd)
    {
        public string PluginId { get; } = pluginId;
        public string MenuId { get; } = menuId;
        public IntPtr Hwnd { get; } = hwnd;
        public FrameworkElement? Element { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; } = 1;
        public int Height { get; set; } = 1;
        public bool Visible { get; set; }
    }
}
