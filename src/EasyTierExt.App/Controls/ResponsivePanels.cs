using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EasyTierExt.App.Controls;

/// <summary>
/// 标签 + 控件的一行：宽度足够时左右排列，不足时自动改为标签在上、控件占满整行，保证任何宽度下都不裁切。
/// 子元素固定为 [0] 标签、[1] 控件。
/// </summary>
internal sealed class LabeledRow : Panel
{
    public double Spacing { get; set; } = 16;
    public double StackSpacing { get; set; } = 6;
    public double MinRowHeight { get; set; } = 36;
    /// <summary>左右排列时标签至少保留的宽度。</summary>
    public double LabelMinWidth { get; set; } = 120;
    /// <summary>控件在上下排列时是否拉伸到整行。</summary>
    public bool StretchControl { get; set; }
    /// <summary>
    /// 可伸缩控件（如会换行的状态文字）：左右排列时最少占用的宽度。为 0 时控件必须按其自然宽度完整显示，
    /// 放不下就改为上下排列。
    /// </summary>
    public double FlexibleMinWidth { get; set; }
    /// <summary>始终上下排列。</summary>
    public bool AlwaysStacked { get; set; }

    private bool _inline;
    private bool? _arrangedInline;
    private double _controlWidth;

    /// <summary>左右排列 / 上下排列切换时触发（参数为是否左右排列）。</summary>
    public event Action<bool>? InlineChanged;

    public LabeledRow(UIElement label, UIElement control)
    {
        Children.Add(label);
        Children.Add(control);
    }

    private UIElement Label => Children[0];
    private UIElement Control => Children[1];

    protected override Size MeasureOverride(Size available)
    {
        double w = available.Width;
        Control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double natural = Control.DesiredSize.Width;

        if (double.IsInfinity(w))
        {
            Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _inline = !AlwaysStacked;
            _controlWidth = natural;
            return _inline
                ? new Size(Label.DesiredSize.Width + Spacing + natural, Math.Max(MinRowHeight, Math.Max(Label.DesiredSize.Height, Control.DesiredSize.Height)))
                : new Size(Math.Max(Label.DesiredSize.Width, natural), Label.DesiredSize.Height + StackSpacing + Control.DesiredSize.Height);
        }

        double room = w - Spacing - LabelMinWidth;
        double controlWidth = FlexibleMinWidth > 0
            ? Math.Min(natural, Math.Max(FlexibleMinWidth, room))
            : natural;
        _inline = !AlwaysStacked && controlWidth <= room + 0.5 && controlWidth > 0;

        if (_inline)
        {
            _controlWidth = controlWidth;
            Control.Measure(new Size(controlWidth, double.PositiveInfinity));
            Label.Measure(new Size(Math.Max(0, w - Spacing - controlWidth), double.PositiveInfinity));
            double h = Math.Max(MinRowHeight, Math.Max(Label.DesiredSize.Height, Control.DesiredSize.Height));
            return new Size(w, h);
        }

        _controlWidth = StretchControl ? w : Math.Min(natural, w);
        Label.Measure(new Size(w, double.PositiveInfinity));
        Control.Measure(new Size(_controlWidth, double.PositiveInfinity));
        bool hasLabel = Label.DesiredSize.Height > 0;
        return new Size(w, Label.DesiredSize.Height + (hasLabel ? StackSpacing : 0) + Control.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_arrangedInline != _inline)
        {
            _arrangedInline = _inline;
            InlineChanged?.Invoke(_inline);
        }

        double w = finalSize.Width;
        if (_inline)
        {
            double h = finalSize.Height;
            double cw = Math.Min(_controlWidth, w);
            double lw = Math.Max(0, w - Spacing - cw);
            Label.Arrange(new Rect(0, (h - Label.DesiredSize.Height) / 2, lw, Label.DesiredSize.Height));
            Control.Arrange(new Rect(w - cw, (h - Control.DesiredSize.Height) / 2, cw, Control.DesiredSize.Height));
        }
        else
        {
            double lh = Label.DesiredSize.Height;
            Label.Arrange(new Rect(0, 0, w, lh));
            double top = lh > 0 ? lh + StackSpacing : 0;
            Control.Arrange(new Rect(0, top, Math.Min(_controlWidth, w), Control.DesiredSize.Height));
        }
        return finalSize;
    }
}

/// <summary>等宽网格：按可用宽度自动决定列数（每项不小于 MinItemWidth），放不下就换行。</summary>
internal sealed class UniformWrapPanel : Panel
{
    public double MinItemWidth { get; set; } = 80;
    public int MaxColumns { get; set; } = int.MaxValue;
    public double ColumnSpacing { get; set; } = 8;
    public double RowSpacing { get; set; } = 8;

    private int _columns = 1;
    private double _itemWidth;
    private readonly List<double> _rowHeights = [];

    private int VisibleCount => Children.Count(c => c.Visibility == Visibility.Visible);

    protected override Size MeasureOverride(Size available)
    {
        int n = VisibleCount;
        _rowHeights.Clear();
        if (n == 0) return new Size(0, 0);

        int maxCols = Math.Max(1, Math.Min(n, MaxColumns));
        if (double.IsInfinity(available.Width))
        {
            _columns = maxCols;
            _itemWidth = MinItemWidth;
        }
        else
        {
            _columns = (int)Math.Floor((available.Width + ColumnSpacing) / (MinItemWidth + ColumnSpacing));
            _columns = Math.Clamp(_columns, 1, maxCols);
            // 行数不变时尽量均分，避免出现 4 + 1 这种最后一行只剩一个的排法
            int rows = (int)Math.Ceiling(n / (double)_columns);
            _columns = (int)Math.Ceiling(n / (double)rows);
            _itemWidth = Math.Max(0, (available.Width - (_columns - 1) * ColumnSpacing) / _columns);
        }

        int index = 0;
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            child.Measure(new Size(_itemWidth, double.PositiveInfinity));
            int row = index / _columns;
            if (row >= _rowHeights.Count) _rowHeights.Add(0);
            _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
            index++;
        }

        double width = double.IsInfinity(available.Width) ? _columns * _itemWidth + (_columns - 1) * ColumnSpacing : available.Width;
        double height = _rowHeights.Sum() + Math.Max(0, _rowHeights.Count - 1) * RowSpacing;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int index = 0;
        double y = 0;
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            int row = index / _columns, col = index % _columns;
            if (col == 0 && row > 0) y += _rowHeights[row - 1] + RowSpacing;
            child.Arrange(new Rect(col * (_itemWidth + ColumnSpacing), y, _itemWidth, _rowHeights[row]));
            index++;
        }
        return finalSize;
    }
}

/// <summary>流式排列：子项保持自然宽度，一行放不下时换到下一行（单项超宽时截断到整行宽度）。</summary>
internal sealed class FlowPanel : Panel
{
    public double ColumnSpacing { get; set; } = 8;
    public double RowSpacing { get; set; } = 8;
    /// <summary>每行内容靠右对齐（用于工具栏按钮）。</summary>
    public bool AlignRight { get; set; }

    private readonly List<(List<UIElement> Items, double Width, double Height)> _lines = [];

    protected override Size MeasureOverride(Size available)
    {
        _lines.Clear();
        double maxW = available.Width;
        var line = new List<UIElement>();
        double lineW = 0, lineH = 0, totalW = 0;

        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            child.Measure(new Size(maxW, double.PositiveInfinity));
            double cw = Math.Min(child.DesiredSize.Width, maxW);
            double next = line.Count == 0 ? cw : lineW + ColumnSpacing + cw;
            if (line.Count > 0 && next > maxW + 0.5)
            {
                _lines.Add((line, lineW, lineH));
                totalW = Math.Max(totalW, lineW);
                line = [];
                lineW = cw;
                lineH = child.DesiredSize.Height;
            }
            else
            {
                lineW = next;
                lineH = Math.Max(lineH, child.DesiredSize.Height);
            }
            line.Add(child);
        }
        if (line.Count > 0)
        {
            _lines.Add((line, lineW, lineH));
            totalW = Math.Max(totalW, lineW);
        }

        double height = _lines.Sum(l => l.Height) + Math.Max(0, _lines.Count - 1) * RowSpacing;
        return new Size(totalW, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var (items, width, height) in _lines)
        {
            double x = AlignRight ? Math.Max(0, finalSize.Width - width) : 0;
            foreach (var child in items)
            {
                double cw = Math.Min(child.DesiredSize.Width, finalSize.Width);
                child.Arrange(new Rect(x, y + (height - child.DesiredSize.Height) / 2, cw, child.DesiredSize.Height));
                x += cw + ColumnSpacing;
            }
            y += height + RowSpacing;
        }
        return finalSize;
    }
}

/// <summary>左右分栏：宽度不小于 Breakpoint 时按比例分两列（两列等高），否则上下堆叠。子元素固定为 [0] 左、[1] 右。</summary>
internal sealed class ResponsiveSplit : Panel
{
    public double Breakpoint { get; set; } = 760;
    public double LeftRatio { get; set; } = 1.45;
    public double RightRatio { get; set; } = 1.0;
    public double Spacing { get; set; } = 20;

    private bool _twoColumns;
    private double _leftWidth;

    public ResponsiveSplit(UIElement left, UIElement right)
    {
        Children.Add(left);
        Children.Add(right);
    }

    protected override Size MeasureOverride(Size available)
    {
        var left = Children[0];
        var right = Children[1];
        double w = double.IsInfinity(available.Width) ? Breakpoint : available.Width;
        _twoColumns = w >= Breakpoint;
        if (_twoColumns)
        {
            _leftWidth = (w - Spacing) * LeftRatio / (LeftRatio + RightRatio);
            double rightWidth = w - Spacing - _leftWidth;
            left.Measure(new Size(_leftWidth, double.PositiveInfinity));
            right.Measure(new Size(rightWidth, double.PositiveInfinity));
            return new Size(w, Math.Max(left.DesiredSize.Height, right.DesiredSize.Height));
        }

        left.Measure(new Size(w, double.PositiveInfinity));
        right.Measure(new Size(w, double.PositiveInfinity));
        return new Size(w, left.DesiredSize.Height + Spacing + right.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var left = Children[0];
        var right = Children[1];
        if (_twoColumns)
        {
            double h = finalSize.Height;
            left.Arrange(new Rect(0, 0, _leftWidth, h));
            right.Arrange(new Rect(_leftWidth + Spacing, 0, finalSize.Width - _leftWidth - Spacing, h));
        }
        else
        {
            left.Arrange(new Rect(0, 0, finalSize.Width, left.DesiredSize.Height));
            right.Arrange(new Rect(0, left.DesiredSize.Height + Spacing, finalSize.Width, right.DesiredSize.Height));
        }
        return finalSize;
    }
}
