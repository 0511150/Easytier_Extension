using System.Text.Json;
using EasyTierExt.Contracts.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace EasyTierExt.App.Controls;

/// <summary>
/// 声明式 UI 的 <c>screen_layout</c> 组件：多台设备的显示器排列画布（主程序 ≥ 1.4.0），格式见《插件开发规范》4.5 节。
/// 同一 group 的显示器整体拖动；拖动时边缘轻微吸附且不允许重叠，松手时若不与其他组相邻则自动贴到最近的边。
/// 点击回传 <c>select</c>（value 为显示器 id），拖动结束回传 <c>move</c>（value 为 <c>{ group, dx, dy }</c>，单位与 x/y 相同）。
/// </summary>
internal sealed class ScreenLayoutView
{
    private const double MinAutoHeight = 260;
    private const double MaxAutoHeight = 460;
    private const double AutoHeightRatio = 0.42;
    private const double FitPadding = 24;
    private const double FitRatio = 0.86;
    private const double SnapPixels = 10;
    private const double DragStartPixels = 4;
    private const double DotSpacing = 22;
    /// <summary>判定两条边相接的容差（布局单位）。</summary>
    private const double Touch = 0.5;

    private static readonly Color CanvasBackground = Color.FromArgb(255, 30, 30, 34);
    private static readonly Color CanvasBorder = Color.FromArgb(255, 46, 46, 53);
    private static readonly Color DotColor = Color.FromArgb(255, 58, 58, 66);
    private static readonly Color TextPrimary = Color.FromArgb(255, 243, 244, 246);
    private static readonly Color TextSecondary = Color.FromArgb(255, 156, 163, 175);

    private readonly Func<string, JsonElement, Task> _raise;
    private readonly Grid _host;
    private readonly Canvas _canvas;
    private readonly Path _dots;
    private readonly TextBlock _placeholder;
    private readonly Dictionary<string, FrameworkElement> _tiles = new(StringComparer.Ordinal);

    private List<ScreenModel> _screens = [];
    private string? _selected;
    private string? _lastRaw;
    private JsonElement? _pending;
    private DragState? _drag;

    // 视图变换：画布坐标 = 偏移 + 布局坐标 × 比例
    private double _scale = 0.1;
    private double _offsetX;
    private double _offsetY;

    public FrameworkElement Root { get; }

    public ScreenLayoutView(UiItem item, JsonElement? initial, Func<string, JsonElement, Task> raise)
    {
        _raise = raise;
        _canvas = new Canvas();
        _dots = new Path { Fill = new SolidColorBrush(DotColor), IsHitTestVisible = false };
        _placeholder = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Placeholder) ? "暂无显示器" : item.Placeholder,
            FontSize = 13,
            Foreground = new SolidColorBrush(TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        bool fixedHeight = item.Max is > 120;
        _host = new Grid
        {
            Height = fixedHeight ? item.Max!.Value : MinAutoHeight,
            Background = new SolidColorBrush(CanvasBackground)
        };
        _host.Children.Add(_dots);
        _host.Children.Add(_canvas);
        _host.Children.Add(_placeholder);
        _host.SizeChanged += (_, _) =>
        {
            _host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _host.ActualWidth, _host.ActualHeight) };
            RebuildDots();
            if (_drag is null)
            {
                Fit();
                Redraw();
            }
        };

        Root = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(CanvasBorder),
            BorderThickness = new Thickness(1),
            Child = _host
        };
        if (!fixedHeight)
        {
            // 未指定 max 时高度随宽度变化，宽窗口下排列不至于太小
            Root.SizeChanged += (_, e) =>
            {
                double h = Math.Clamp(e.NewSize.Width * AutoHeightRatio, MinAutoHeight, MaxAutoHeight);
                if (Math.Abs(_host.Height - h) > 1) _host.Height = h;
            };
        }

        if (initial is { } v) Update(v);
    }

    public void Update(JsonElement value)
    {
        // 拖动过程中先不刷新，松手后由插件按新位置重新下发
        if (_drag is not null)
        {
            _pending = value.Clone();
            return;
        }

        string raw = value.GetRawText();
        if (raw == _lastRaw) return;
        _lastRaw = raw;

        (_screens, _selected) = Parse(value);
        Fit();
        Redraw();
    }

    // ---------------- 解析 ----------------

    private static (List<ScreenModel> Screens, string? Selected) Parse(JsonElement v)
    {
        var list = new List<ScreenModel>();
        string? selected = null;
        if (v.ValueKind != JsonValueKind.Object) return (list, selected);

        if (v.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.String) selected = sel.GetString();
        if (!v.TryGetProperty("screens", out var arr) || arr.ValueKind != JsonValueKind.Array) return (list, selected);

        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var m = new ScreenModel
            {
                Id = Str(s, "id"),
                Number = Str(s, "number"),
                Title = Str(s, "title"),
                Subtitle = Str(s, "subtitle"),
                Tone = Str(s, "tone"),
                X = Num(s, "x"),
                Y = Num(s, "y"),
                W = Num(s, "w"),
                H = Num(s, "h"),
                Active = s.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True,
                Locked = s.TryGetProperty("locked", out var l) && l.ValueKind == JsonValueKind.True
            };
            m.Group = s.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? m.Id : m.Id;
            if (string.IsNullOrEmpty(m.Id) || m.W <= 0 || m.H <= 0) continue;
            list.Add(m);
        }
        return (list, selected);

        static string Str(JsonElement o, string k) =>
            o.TryGetProperty(k, out var p) ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? string.Empty,
                JsonValueKind.Number => p.GetRawText(),
                _ => string.Empty
            } : string.Empty;

        static double Num(JsonElement o, string k) =>
            o.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;
    }

    // ---------------- 绘制 ----------------

    private void Fit()
    {
        double w = _host.ActualWidth, h = _host.ActualHeight;
        if (w <= 0 || h <= 0 || _screens.Count == 0) return;

        double minX = _screens.Min(s => s.X), minY = _screens.Min(s => s.Y);
        double maxX = _screens.Max(s => s.X + s.W), maxY = _screens.Max(s => s.Y + s.H);
        double bw = Math.Max(maxX - minX, 1), bh = Math.Max(maxY - minY, 1);

        double availW = Math.Max(w - 2 * FitPadding, 40) * FitRatio;
        double availH = Math.Max(h - 2 * FitPadding, 40) * FitRatio;
        _scale = Math.Min(availW / bw, availH / bh);
        _offsetX = (w - bw * _scale) / 2 - minX * _scale;
        _offsetY = (h - bh * _scale) / 2 - minY * _scale;
    }

    private void RebuildDots()
    {
        double w = _host.ActualWidth, h = _host.ActualHeight;
        var group = new GeometryGroup();
        for (double y = DotSpacing / 2; y < h; y += DotSpacing)
        {
            for (double x = DotSpacing / 2; x < w; x += DotSpacing)
            {
                group.Children.Add(new RectangleGeometry { Rect = new Rect(x - 1, y - 1, 2, 2) });
            }
        }
        _dots.Data = group;
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        _tiles.Clear();
        _placeholder.Visibility = _screens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 选中项最后绘制，保证粗边框不被相邻显示器遮住
        foreach (var s in _screens.OrderBy(s => s.Id == _selected ? 1 : 0))
        {
            var tile = CreateTile(s);
            _tiles[s.Id] = tile;
            _canvas.Children.Add(tile);
            PlaceTile(tile, s.X, s.Y);
        }
    }

    private void PlaceTile(FrameworkElement tile, double x, double y)
    {
        Canvas.SetLeft(tile, _offsetX + x * _scale);
        Canvas.SetTop(tile, _offsetY + y * _scale);
    }

    private FrameworkElement CreateTile(ScreenModel s)
    {
        bool selected = s.Id == _selected;
        var (border, borderSelected, fill) = ToneColors(s.Tone);
        double w = Math.Max(s.W * _scale, 6), h = Math.Max(s.H * _scale, 6);
        double size = Math.Min(w, h);

        var tile = new ScreenTile(!s.Locked)
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(Math.Min(6, size / 6)),
            Background = new SolidColorBrush(fill),
            BorderBrush = new SolidColorBrush(selected ? borderSelected : border),
            BorderThickness = new Thickness(selected ? 3 : 1.5)
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            IsHitTestVisible = false
        };
        double maxTextWidth = Math.Max(w - 12, 10);
        if (!string.IsNullOrEmpty(s.Number) && size >= 22)
        {
            stack.Children.Add(Text(s.Number, Math.Clamp(size * 0.26, 11, 38), FontWeights.Bold, TextPrimary, maxTextWidth));
        }
        if (!string.IsNullOrEmpty(s.Title) && h >= 64 && w >= 70)
        {
            stack.Children.Add(Text(s.Title, Math.Clamp(size * 0.105, 10.5, 17), FontWeights.Normal, TextPrimary, maxTextWidth));
        }
        if (!string.IsNullOrEmpty(s.Subtitle) && h >= 92 && w >= 90)
        {
            stack.Children.Add(Text(s.Subtitle, Math.Clamp(size * 0.08, 9.5, 13), FontWeights.Normal, TextSecondary, maxTextWidth));
        }
        tile.Children.Add(stack);

        if (s.Active && size >= 48)
        {
            var cursor = CreateCursorGlyph(Math.Clamp(size * 0.13, 12, 26));
            cursor.HorizontalAlignment = HorizontalAlignment.Left;
            cursor.VerticalAlignment = VerticalAlignment.Top;
            cursor.Margin = new Thickness(Math.Min(w * 0.7, w - 24), Math.Min(h * 0.72, h - 28), 0, 0);
            tile.Children.Add(cursor);
        }

        tile.PointerPressed += (_, e) => OnPointerPressed(tile, s, e);
        tile.PointerMoved += (_, e) => OnPointerMoved(e);
        tile.PointerReleased += (_, e) => OnPointerReleased(tile, e);
        tile.PointerCaptureLost += (_, e) => OnPointerReleased(tile, e);
        return tile;
    }

    private static TextBlock Text(string text, double size, Windows.UI.Text.FontWeight weight, Color color, double maxWidth) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = new SolidColorBrush(color),
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = maxWidth,
        IsHitTestVisible = false
    };

    private static Path CreateCursorGlyph(double height)
    {
        // 标准箭头光标轮廓（以 20 为高度绘制后缩放）
        var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true, IsFilled = true };
        foreach (var (x, y) in new[] { (0.0, 17.0), (4.6, 13.2), (7.6, 19.6), (10.4, 18.4), (7.5, 12.1), (13.0, 12.1) })
        {
            figure.Segments.Add(new LineSegment { Point = new Point(x, y) });
        }
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        double k = height / 20;
        geometry.Transform = new ScaleTransform { ScaleX = k, ScaleY = k };
        return new Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 20, 20, 24)),
            StrokeThickness = 1.2,
            IsHitTestVisible = false
        };
    }

    private static (Color Border, Color BorderSelected, Color Fill) ToneColors(string tone) => tone switch
    {
        "self" => (Color.FromArgb(255, 59, 130, 246), Color.FromArgb(255, 96, 165, 250), Color.FromArgb(255, 36, 48, 74)),
        "offline" => (Color.FromArgb(255, 82, 86, 96), Color.FromArgb(255, 156, 163, 175), Color.FromArgb(255, 40, 41, 46)),
        _ => (Color.FromArgb(255, 20, 160, 140), Color.FromArgb(255, 45, 212, 191), Color.FromArgb(255, 30, 56, 54))
    };

    // ---------------- 拖动 ----------------

    private void OnPointerPressed(FrameworkElement tile, ScreenModel s, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_host);
        if (!point.Properties.IsLeftButtonPressed || _drag is not null) return;
        tile.CapturePointer(e.Pointer);
        _drag = new DragState
        {
            ScreenId = s.Id,
            Group = s.Group,
            Start = point.Position,
            Movable = !s.Locked
        };
        e.Handled = true;
    }

    private void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (_drag is null || !_drag.Movable) return;
        var p = e.GetCurrentPoint(_host).Position;
        double px = p.X - _drag.Start.X, py = p.Y - _drag.Start.Y;
        if (!_drag.Moving)
        {
            if (Math.Abs(px) < DragStartPixels && Math.Abs(py) < DragStartPixels) return;
            _drag.Moving = true;
        }

        var (dx, dy) = Constrain(_drag.Group, px / _scale, py / _scale);
        if (Overlaps(_drag.Group, dx, dy))
        {
            (dx, dy) = (_drag.Dx, _drag.Dy);
        }
        _drag.Dx = dx;
        _drag.Dy = dy;
        foreach (var s in _screens.Where(s => s.Group == _drag.Group))
        {
            if (_tiles.TryGetValue(s.Id, out var tile)) PlaceTile(tile, s.X + dx, s.Y + dy);
        }
        e.Handled = true;
    }

    private void OnPointerReleased(FrameworkElement tile, PointerRoutedEventArgs e)
    {
        var drag = _drag;
        if (drag is null) return;
        _drag = null;
        tile.ReleasePointerCaptures();
        _pending = null;
        e.Handled = true;

        if (!drag.Moving)
        {
            if (_selected != drag.ScreenId)
            {
                _selected = drag.ScreenId;
                Redraw();
            }
            _ = _raise("select", JsonSerializer.SerializeToElement(drag.ScreenId));
            return;
        }

        var (dx, dy) = Attach(drag.Group, drag.Dx, drag.Dy);
        if (Overlaps(drag.Group, dx, dy)) (dx, dy) = (drag.Dx, drag.Dy);
        foreach (var s in _screens.Where(s => s.Group == drag.Group))
        {
            s.X += dx;
            s.Y += dy;
        }
        _selected = drag.ScreenId;
        _lastRaw = null;
        Fit();
        Redraw();

        if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
        {
            _ = _raise("move", JsonSerializer.SerializeToElement(new { group = drag.Group, dx = Math.Round(dx, 2), dy = Math.Round(dy, 2) }));
        }
        else
        {
            _ = _raise("select", JsonSerializer.SerializeToElement(drag.ScreenId));
        }
    }

    /// <summary>拖动中的位置修正：先轻微吸附到附近的边，再把重叠部分推开。</summary>
    private (double Dx, double Dy) Constrain(string group, double dx, double dy)
    {
        var (mine, others) = Split(group);
        if (others.Count == 0) return (dx, dy);

        double thr = SnapPixels / _scale;
        double? snapX = null, snapY = null;
        foreach (var a in mine)
        {
            double ax = a.X + dx, ay = a.Y + dy, ar = ax + a.W, ab = ay + a.H;
            foreach (var b in others)
            {
                double br = b.X + b.W, bb = b.Y + b.H;
                bool yOverlapNear = ay < bb + thr && b.Y < ab + thr;
                bool xOverlapNear = ax < br + thr && b.X < ar + thr;
                bool sideBySide = Math.Abs(ax - br) < thr || Math.Abs(ar - b.X) < thr;
                bool stacked = Math.Abs(ay - bb) < thr || Math.Abs(ab - b.Y) < thr;

                if (yOverlapNear)
                {
                    Consider(ref snapX, br - ax, thr);
                    Consider(ref snapX, b.X - ar, thr);
                }
                if (xOverlapNear)
                {
                    Consider(ref snapY, bb - ay, thr);
                    Consider(ref snapY, b.Y - ab, thr);
                }
                if (sideBySide && yOverlapNear)
                {
                    Consider(ref snapY, b.Y - ay, thr);
                    Consider(ref snapY, bb - ab, thr);
                }
                if (stacked && xOverlapNear)
                {
                    Consider(ref snapX, b.X - ax, thr);
                    Consider(ref snapX, br - ar, thr);
                }
            }
        }
        dx += snapX ?? 0;
        dy += snapY ?? 0;

        // 把重叠推开：每次沿穿透最浅的方向移出
        for (int iter = 0; iter < 12; iter++)
        {
            bool pushed = false;
            foreach (var a in mine)
            {
                double ax = a.X + dx, ay = a.Y + dy, ar = ax + a.W, ab = ay + a.H;
                foreach (var b in others)
                {
                    double br = b.X + b.W, bb = b.Y + b.H;
                    if (Math.Min(ar, br) - Math.Max(ax, b.X) <= Touch || Math.Min(ab, bb) - Math.Max(ay, b.Y) <= Touch) continue;

                    double right = br - ax, left = ar - b.X, down = bb - ay, up = ab - b.Y;
                    double min = Math.Min(Math.Min(right, left), Math.Min(down, up));
                    if (min == right) dx += right;
                    else if (min == left) dx -= left;
                    else if (min == down) dy += down;
                    else dy -= up;
                    pushed = true;
                    break;
                }
                if (pushed) break;
            }
            if (!pushed) break;
        }
        return (dx, dy);
    }

    /// <summary>松手时若与其他组都不相邻，贴到移动距离最短且不重叠的一条边上。</summary>
    private (double Dx, double Dy) Attach(string group, double dx, double dy)
    {
        var (mine, others) = Split(group);
        if (others.Count == 0 || Touching(mine, others, dx, dy)) return (dx, dy);

        double bestCost = double.MaxValue;
        (double, double)? best = null;
        foreach (var a in mine)
        {
            double ax = a.X + dx, ay = a.Y + dy;
            foreach (var b in others)
            {
                double ty = AlignInto(ay, a.H, b.Y, b.H);
                double tx = AlignInto(ax, a.W, b.X, b.W);
                var candidates = new[]
                {
                    (b.X + b.W - ax, ty),   // 放在 b 右侧
                    (b.X - (ax + a.W), ty), // 放在 b 左侧
                    (tx, b.Y + b.H - ay),   // 放在 b 下方
                    (tx, b.Y - (ay + a.H))  // 放在 b 上方
                };
                foreach (var (cx, cy) in candidates)
                {
                    double cost = Math.Sqrt(cx * cx + cy * cy);
                    if (cost < bestCost && !Overlaps(group, dx + cx, dy + cy))
                    {
                        bestCost = cost;
                        best = (dx + cx, dy + cy);
                    }
                }
            }
        }
        return best ?? (dx, dy);
    }

    /// <summary>沿一个轴平移多少，能让 [start, start+len] 与 [bStart, bStart+bLen] 至少重叠两者较短者的 30%。</summary>
    private static double AlignInto(double start, double len, double bStart, double bLen)
    {
        double need = Math.Min(len, bLen) * 0.3;
        if (start + len < bStart + need) return bStart + need - (start + len);
        if (start > bStart + bLen - need) return bStart + bLen - need - start;
        return 0;
    }

    private static bool Touching(List<ScreenModel> mine, List<ScreenModel> others, double dx, double dy)
    {
        foreach (var a in mine)
        {
            double ax = a.X + dx, ay = a.Y + dy, ar = ax + a.W, ab = ay + a.H;
            foreach (var b in others)
            {
                double br = b.X + b.W, bb = b.Y + b.H;
                bool yOverlap = Math.Min(ab, bb) - Math.Max(ay, b.Y) > Touch;
                bool xOverlap = Math.Min(ar, br) - Math.Max(ax, b.X) > Touch;
                if (yOverlap && (Math.Abs(ar - b.X) <= Touch * 2 || Math.Abs(ax - br) <= Touch * 2)) return true;
                if (xOverlap && (Math.Abs(ab - b.Y) <= Touch * 2 || Math.Abs(ay - bb) <= Touch * 2)) return true;
            }
        }
        return false;
    }

    private bool Overlaps(string group, double dx, double dy)
    {
        var (mine, others) = Split(group);
        foreach (var a in mine)
        {
            double ax = a.X + dx, ay = a.Y + dy;
            foreach (var b in others)
            {
                if (Math.Min(ax + a.W, b.X + b.W) - Math.Max(ax, b.X) > Touch &&
                    Math.Min(ay + a.H, b.Y + b.H) - Math.Max(ay, b.Y) > Touch) return true;
            }
        }
        return false;
    }

    private (List<ScreenModel> Mine, List<ScreenModel> Others) Split(string group) =>
        (_screens.Where(s => s.Group == group).ToList(), _screens.Where(s => s.Group != group).ToList());

    private static void Consider(ref double? best, double delta, double threshold)
    {
        if (Math.Abs(delta) <= threshold && (best is null || Math.Abs(delta) < Math.Abs(best.Value))) best = delta;
    }

    private sealed class ScreenModel
    {
        public string Id { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Tone { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public bool Active { get; set; }
        public bool Locked { get; set; }
    }

    private sealed class DragState
    {
        public string ScreenId { get; init; } = string.Empty;
        public string Group { get; init; } = string.Empty;
        public Point Start { get; init; }
        public bool Movable { get; init; }
        public bool Moving { get; set; }
        public double Dx { get; set; }
        public double Dy { get; set; }
    }

    /// <summary>可设置鼠标指针形状的 Grid（ProtectedCursor 只能由子类设置）。</summary>
    private sealed class ScreenTile : Grid
    {
        public ScreenTile(bool movable)
        {
            ProtectedCursor = InputSystemCursor.Create(movable ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Hand);
        }
    }
}
