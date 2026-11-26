using System.Globalization;
using System.Text.Json;
using EasyTierExt.Contracts.Models;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace EasyTierExt.App.Controls;

/// <summary>
/// 声明式 UI 的 <c>chart</c> 组件：平滑折线 + 渐变填充 + 均值虚线 + 悬停提示 + 底部统计。
/// 数据通过 item.value 下发，格式见《插件开发规范》4.3 节。
/// </summary>
internal sealed class TrafficChartView
{
    private const double PlotHeight = 210;

    private static readonly Color TextPrimary = Color.FromArgb(255, 243, 244, 246);
    private static readonly Color TextSecondary = Color.FromArgb(255, 156, 163, 175);
    private static readonly Color GridColor = Color.FromArgb(255, 58, 58, 66);
    private static readonly Color DividerColor = Color.FromArgb(255, 46, 46, 53);

    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly FlowPanel _legend;
    private readonly Canvas _plot;
    private readonly Canvas _overlay;
    private readonly Canvas _axis;
    private readonly Border _tooltip;
    private readonly StackPanel _tooltipStack;
    private readonly UniformWrapPanel _stats;
    private readonly Border _statsDivider;
    private readonly string _fallbackSubtitle;

    private ChartData _data = new();
    private double? _hoverX;

    public FrameworkElement Root { get; }

    public TrafficChartView(UiItem item, JsonElement? initial)
    {
        _fallbackSubtitle = item.Subtitle ?? string.Empty;

        var root = new Grid { RowSpacing = 10 };
        for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题与图例
        var titles = new StackPanel { Spacing = 4 };
        _title = new TextBlock { Text = item.Label, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brush(TextPrimary) };
        _subtitle = new TextBlock { FontSize = 12.5, Foreground = Brush(TextSecondary), TextWrapping = TextWrapping.Wrap };
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        // 图例放不下时换到标题下方
        _legend = new FlowPanel { ColumnSpacing = 22, RowSpacing = 4 };
        root.Children.Add(new LabeledRow(titles, _legend) { LabelMinWidth = 160, MinRowHeight = 0, StackSpacing = 8 });

        // 绘图区
        var plotHost = new Grid { Height = PlotHeight, Background = new SolidColorBrush(Colors.Transparent) };
        _plot = new Canvas();
        _overlay = new Canvas { IsHitTestVisible = false };
        _tooltipStack = new StackPanel { Spacing = 4 };
        _tooltip = new Border
        {
            Background = Brush(Color.FromArgb(240, 38, 38, 44)),
            BorderBrush = Brush(Color.FromArgb(255, 58, 58, 66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = _tooltipStack,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        plotHost.Children.Add(_plot);
        plotHost.Children.Add(_overlay);
        var tooltipCanvas = new Canvas { IsHitTestVisible = false };
        tooltipCanvas.Children.Add(_tooltip);
        plotHost.Children.Add(tooltipCanvas);
        plotHost.SizeChanged += (_, _) => Redraw();
        plotHost.PointerMoved += (_, e) =>
        {
            _hoverX = e.GetCurrentPoint(_plot).Position.X;
            ShowHover(_hoverX.Value);
        };
        plotHost.PointerExited += (_, _) =>
        {
            _hoverX = null;
            HideHover();
        };
        Grid.SetRow(plotHost, 1);
        root.Children.Add(plotHost);

        // 横轴刻度
        _axis = new Canvas { Height = 16 };
        Grid.SetRow(_axis, 2);
        root.Children.Add(_axis);

        // 底部统计
        _statsDivider = new Border { Height = 1, Background = Brush(DividerColor), Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(_statsDivider, 3);
        root.Children.Add(_statsDivider);
        _stats = new UniformWrapPanel { MinItemWidth = 150, ColumnSpacing = 0, RowSpacing = 6 };
        Grid.SetRow(_stats, 4);
        root.Children.Add(_stats);

        Root = root;
        if (initial is { } v) Update(v);
        else Apply(new ChartData());
    }

    public void Update(JsonElement value) => Apply(ChartData.Parse(value));

    private void Apply(ChartData data)
    {
        _data = data;
        _subtitle.Text = data.Subtitle ?? _fallbackSubtitle;
        _subtitle.Visibility = string.IsNullOrEmpty(_subtitle.Text) ? Visibility.Collapsed : Visibility.Visible;
        RebuildLegend();
        RebuildStats();
        Redraw();
    }

    private void RebuildLegend()
    {
        _legend.Children.Clear();
        foreach (var s in _data.Series)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (!string.IsNullOrEmpty(s.Icon))
                row.Children.Add(new TextBlock { Text = s.Icon, FontSize = 13, Foreground = Brush(s.Color), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = s.Name, FontSize = 13, Foreground = Brush(TextSecondary), VerticalAlignment = VerticalAlignment.Center });
            string value = s.Legend ?? (s.Points.Count > 0 ? _data.FormatY(s.Points[^1].Y) : "-");
            row.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush(TextPrimary), VerticalAlignment = VerticalAlignment.Center });
            _legend.Children.Add(row);
        }
    }

    private void RebuildStats()
    {
        _stats.Children.Clear();
        bool any = _data.Stats.Count > 0;
        _stats.Visibility = _statsDivider.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _data.Stats.Count; i++)
        {
            var cell = new StackPanel { Spacing = 6, Padding = new Thickness(14, 6, 12, 2) };
            cell.Children.Add(new TextBlock { Text = _data.Stats[i].Label, FontSize = 12.5, Foreground = Brush(TextSecondary) });
            cell.Children.Add(new TextBlock { Text = _data.Stats[i].Value, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brush(TextPrimary), TextWrapping = TextWrapping.Wrap });
            var border = new Border
            {
                Child = cell,
                BorderBrush = Brush(DividerColor),
                BorderThickness = new Thickness(1, 0, 0, 0)
            };
            _stats.Children.Add(border);
        }
    }

    private (double W, double H, double XMax, double YMax) Scale()
    {
        double w = _plot.ActualWidth;
        double xMax = _data.XMax ?? Math.Max(1, _data.Series.SelectMany(s => s.Points).Select(p => p.X).DefaultIfEmpty(1).Max());
        double yMax = _data.Series.SelectMany(s => s.Points.Select(p => p.Y).Append(s.Mark ?? 0)).DefaultIfEmpty(0).Max();
        yMax = yMax <= 0 ? 1 : yMax * 1.15;
        return (w, PlotHeight - 6, xMax, yMax);
    }

    private void Redraw()
    {
        _plot.Children.Clear();
        _axis.Children.Clear();
        HideHover();
        var (w, h, xMax, yMax) = Scale();
        if (w <= 0) return;

        // 横向虚线网格
        for (int i = 0; i <= 4; i++)
        {
            double y = 3 + h * i / 4;
            _plot.Children.Add(new Line
            {
                X1 = 0, X2 = w, Y1 = y, Y2 = y,
                Stroke = Brush(GridColor),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            });
        }

        foreach (var s in _data.Series)
        {
            if (s.Mark is { } mark && mark > 0)
            {
                double my = 3 + h - mark / yMax * h;
                _plot.Children.Add(new Line
                {
                    X1 = 0, X2 = w, Y1 = my, Y2 = my,
                    Stroke = Brush(s.Color),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 1, 2.5 },
                    Opacity = 0.9
                });
            }

            if (s.Points.Count == 0) continue;
            var pts = s.Points.Select(p => new Point(p.X / xMax * w, 3 + h - p.Y / yMax * h)).ToList();
            double baseline = 3 + h;

            var fillFigure = new PathFigure { StartPoint = new Point(pts[0].X, baseline), IsClosed = true };
            fillFigure.Segments.Add(new LineSegment { Point = pts[0] });
            AppendSmooth(fillFigure, pts, 3, baseline);
            fillFigure.Segments.Add(new LineSegment { Point = new Point(pts[^1].X, baseline) });
            var fillGeo = new PathGeometry();
            fillGeo.Figures.Add(fillFigure);
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            gradient.GradientStops.Add(new GradientStop { Color = WithAlpha(s.Color, 70), Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = WithAlpha(s.Color, 0), Offset = 1 });
            _plot.Children.Add(new Path { Data = fillGeo, Fill = gradient });

            var lineFigure = new PathFigure { StartPoint = pts[0] };
            AppendSmooth(lineFigure, pts, 3, baseline);
            var lineGeo = new PathGeometry();
            lineGeo.Figures.Add(lineFigure);
            _plot.Children.Add(new Path { Data = lineGeo, Stroke = Brush(s.Color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
        }

        // 横轴刻度：按相对时间 0 ~ x_max
        double step = NiceStep(xMax);
        for (double x = 0; x <= xMax + 1e-9; x += step)
        {
            var label = new TextBlock
            {
                Text = $"{x.ToString("0.##", CultureInfo.InvariantCulture)}{_data.XUnit}",
                FontSize = 11,
                Foreground = Brush(TextSecondary)
            };
            _axis.Children.Add(label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = x / xMax * w - label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, Math.Clamp(left, 0, Math.Max(0, w - label.DesiredSize.Width)));
        }

        // 数据实时刷新时保持悬停提示
        if (_hoverX is { } hx) ShowHover(hx);
    }

    /// <summary>Catmull-Rom 转三次贝塞尔，控制点限制在绘图区内，避免曲线越过基线。</summary>
    private static void AppendSmooth(PathFigure figure, List<Point> pts, double top, double bottom)
    {
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, top, bottom));
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, top, bottom));
            figure.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = p2 });
        }
    }

    private void ShowHover(double px)
    {
        var (w, h, xMax, yMax) = Scale();
        var withPoints = _data.Series.Where(s => s.Points.Count > 0).ToList();
        if (w <= 0 || withPoints.Count == 0)
        {
            HideHover();
            return;
        }

        double x = Math.Clamp(px / w * xMax, 0, xMax);
        _overlay.Children.Clear();
        _tooltipStack.Children.Clear();

        double lineX = 0;
        bool first = true;
        foreach (var s in withPoints)
        {
            var nearest = s.Points.MinBy(p => Math.Abs(p.X - x))!;
            if (first)
            {
                lineX = nearest.X / xMax * w;
                _tooltipStack.Children.Add(new TextBlock
                {
                    Text = $"{nearest.X.ToString("0.0", CultureInfo.InvariantCulture)}{_data.XUnit}",
                    FontSize = 12.5,
                    Foreground = Brush(TextSecondary)
                });
                first = false;
            }
            double cy = 3 + h - nearest.Y / yMax * h;
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Brush(s.Color), Stroke = Brush(Color.FromArgb(255, 34, 34, 38)), StrokeThickness = 1.5 };
            Canvas.SetLeft(dot, nearest.X / xMax * w - 4);
            Canvas.SetTop(dot, cy - 4);
            _overlay.Children.Add(dot);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brush(s.Color), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = s.Name, FontSize = 13, Foreground = Brush(TextPrimary) });
            row.Children.Add(new TextBlock { Text = _data.FormatY(nearest.Y), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush(TextPrimary) });
            _tooltipStack.Children.Add(row);
        }

        _overlay.Children.Insert(0, new Line
        {
            X1 = lineX, X2 = lineX, Y1 = 0, Y2 = PlotHeight,
            Stroke = Brush(Color.FromArgb(120, 156, 163, 175)),
            StrokeThickness = 1
        });

        _tooltip.Visibility = Visibility.Visible;
        _tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = _tooltip.DesiredSize.Width;
        double left = lineX + 14 + tw > w ? lineX - 14 - tw : lineX + 14;
        Canvas.SetLeft(_tooltip, Math.Max(0, left));
        Canvas.SetTop(_tooltip, 8);
    }

    private void HideHover()
    {
        _overlay.Children.Clear();
        _tooltip.Visibility = Visibility.Collapsed;
    }

    private static double NiceStep(double max)
    {
        foreach (var step in new[] { 1.0, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
        {
            if (max / step <= 8) return step;
        }
        return Math.Ceiling(max / 8);
    }

    private static SolidColorBrush Brush(Color c) => new(c);

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private sealed record ChartPoint(double X, double Y);

    private sealed class ChartSeries
    {
        public string Name { get; init; } = string.Empty;
        public string? Icon { get; init; }
        public Color Color { get; init; }
        public double? Mark { get; init; }
        public string? Legend { get; init; }
        public List<ChartPoint> Points { get; init; } = [];
    }

    private sealed class ChartData
    {
        private static readonly Color[] Palette =
        [
            Color.FromArgb(255, 59, 130, 246),
            Color.FromArgb(255, 156, 163, 175),
            Color.FromArgb(255, 16, 185, 129),
            Color.FromArgb(255, 245, 158, 11)
        ];

        public string? Subtitle { get; private init; }
        public double? XMax { get; private init; }
        public string XUnit { get; private init; } = string.Empty;
        public string YFormat { get; private init; } = "number";
        public string YUnit { get; private init; } = string.Empty;
        public List<ChartSeries> Series { get; private init; } = [];
        public List<(string Label, string Value)> Stats { get; private init; } = [];

        public static ChartData Parse(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Object) return new ChartData();
            var series = new List<ChartSeries>();
            if (v.TryGetProperty("series", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var s in arr.EnumerateArray())
                {
                    var points = new List<ChartPoint>();
                    if (s.TryGetProperty("points", out var pa) && pa.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in pa.EnumerateArray())
                        {
                            if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 &&
                                p[0].TryGetDouble(out var px) && p[1].TryGetDouble(out var py))
                            {
                                points.Add(new ChartPoint(px, Math.Max(0, py)));
                            }
                        }
                    }
                    series.Add(new ChartSeries
                    {
                        Name = Str(s, "name") ?? $"系列 {idx + 1}",
                        Icon = Str(s, "icon"),
                        Color = ParseColor(Str(s, "color")) ?? Palette[idx % Palette.Length],
                        Mark = Num(s, "mark"),
                        Legend = Str(s, "legend"),
                        Points = points
                    });
                    idx++;
                }
            }

            var stats = new List<(string, string)>();
            if (v.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in st.EnumerateArray())
                    stats.Add((Str(s, "label") ?? string.Empty, Str(s, "value") ?? "-"));
            }

            return new ChartData
            {
                Subtitle = Str(v, "subtitle"),
                XMax = Num(v, "x_max") is double xm && xm > 0 ? xm : null,
                XUnit = Str(v, "x_unit") ?? string.Empty,
                YFormat = Str(v, "y_format") ?? "number",
                YUnit = Str(v, "y_unit") ?? string.Empty,
                Series = series,
                Stats = stats
            };
        }

        public string FormatY(double y) => YFormat switch
        {
            "bps" => FormatScaled(y, 1000, ["bps", "Kbps", "Mbps", "Gbps"]),
            "Bps" => FormatScaled(y, 1024, ["B/s", "KB/s", "MB/s", "GB/s"]),
            _ => $"{y.ToString("0.##", CultureInfo.InvariantCulture)} {YUnit}".TrimEnd()
        };

        private static string FormatScaled(double value, double k, string[] units)
        {
            int i = 0;
            while (value >= k && i < units.Length - 1)
            {
                value /= k;
                i++;
            }
            return $"{value.ToString(i == 0 ? "0" : "0.00", CultureInfo.InvariantCulture)} {units[i]}";
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static double? Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

        private static Color? ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            var s = hex.Trim().TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            return s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)
                ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
                : null;
        }
    }
}
