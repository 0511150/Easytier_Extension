using System.IO;
using System.Text.Json;
using EasyTierExt.App.Native;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace EasyTierExt.App.Controls;

/// <summary>
/// 把插件提交的声明式 Schema 渲染为 WinUI 界面。所有控件都按可用宽度自适应：
/// 标签与控件放不下时自动上下排列，按钮组与卡片自动换行，表格过宽时横向滚动，任何窗口宽度下都不裁切内容。
/// </summary>
public sealed class DeclarativePluginRenderer
{
    private static readonly Color CardBackground = Color.FromArgb(255, 34, 34, 38);
    private static readonly Color CardBorder = Color.FromArgb(255, 46, 46, 53);
    private static readonly Color PaneBackground = Color.FromArgb(255, 26, 26, 30);
    private static readonly Color PaneBorder = Color.FromArgb(255, 42, 42, 50);
    private static readonly Color TextTitle = Color.FromArgb(255, 243, 244, 246);
    private static readonly Color TextNormal = Color.FromArgb(255, 229, 231, 235);
    private static readonly Color TextBody = Color.FromArgb(255, 209, 213, 219);
    private static readonly Color TextSecondary = Color.FromArgb(255, 156, 163, 175);
    private static readonly Color TextMuted = Color.FromArgb(255, 107, 114, 128);
    private static readonly Color Accent = Color.FromArgb(255, 59, 130, 246);
    private static readonly Color AccentBorder = Color.FromArgb(255, 96, 165, 250);
    private static readonly Color DangerColor = Color.FromArgb(255, 220, 38, 38);
    private static readonly Color ControlBackground = Color.FromArgb(255, 42, 42, 48);
    private static readonly Color ControlBorder = Color.FromArgb(255, 58, 58, 68);

    private readonly Func<string, string, EventUiActionParams, Task> _onUiAction;
    private readonly ViewportHostController _viewportController;
    private readonly Dictionary<string, Action<JsonElement>> _updaters = new(StringComparer.Ordinal);
    private bool _suppressEvents;

    public DeclarativePluginRenderer(
        Func<string, string, EventUiActionParams, Task> onUiAction,
        ViewportHostController viewportController)
    {
        _onUiAction = onUiAction;
        _viewportController = viewportController;
    }

    public UIElement Render(
        string pluginId,
        string menuId,
        UiPageSchema schema,
        Dictionary<string, JsonElement>? cachedValues)
    {
        _updaters.Clear();

        var rootGrid = new Grid { RowSpacing = 12 };
        var normalStack = new StackPanel { Spacing = 14 };
        bool hasViewport = false;
        UIElement? viewportSectionElement = null;

        foreach (var section in schema.Sections)
        {
            var card = CreateSectionCard(pluginId, menuId, section, cachedValues, out bool containsViewport);
            if (containsViewport)
            {
                hasViewport = true;
                viewportSectionElement = card;
            }
            else
            {
                normalStack.Children.Add(card);
            }
        }

        if (!hasViewport)
        {
            _viewportController.HideAll();
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = normalStack
            };
        }

        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (normalStack.Children.Count > 0)
        {
            Grid.SetRow(normalStack, 0);
            rootGrid.Children.Add(normalStack);
        }

        if (viewportSectionElement is FrameworkElement vpFrameworkEl)
        {
            Grid.SetRow(vpFrameworkEl, 1);
            rootGrid.Children.Add(vpFrameworkEl);
        }

        return rootGrid;
    }

    public void ApplyValues(Dictionary<string, JsonElement> values)
    {
        _suppressEvents = true;
        try
        {
            foreach (var (key, value) in values)
            {
                if (_updaters.TryGetValue(key, out var updater))
                {
                    updater(value);
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    // ---------------- 分组卡片 ----------------

    private Border CreateSectionCard(
        string pluginId,
        string menuId,
        UiSection section,
        Dictionary<string, JsonElement>? cachedValues,
        out bool containsViewport)
    {
        containsViewport = section.Items.Any(i => i.Type == "viewport");

        var card = new Border
        {
            Background = Brush(CardBackground),
            BorderBrush = Brush(CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = section.Compact ? new Thickness(14, 10, 14, 10) : new Thickness(18, 16, 18, 16)
        };

        if (containsViewport)
        {
            card.Child = CreateViewportSection(pluginId, menuId, section);
            return card;
        }

        var stack = new StackPanel { Spacing = section.Compact ? 8 : 12 };
        if (!string.IsNullOrWhiteSpace(section.Title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(TextTitle),
                Margin = new Thickness(0, 0, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (section.Compact)
        {
            stack.Children.Add(CreateCompactToolbar(pluginId, menuId, section, cachedValues));
        }
        else if (string.Equals(section.Layout, "split", StringComparison.OrdinalIgnoreCase))
        {
            stack.Children.Add(CreateSplitLayout(pluginId, menuId, section, cachedValues));
        }
        else
        {
            foreach (var item in section.Items)
            {
                stack.Children.Add(CreateItem(pluginId, menuId, item, ResolveValue(item, cachedValues)));
            }
        }

        card.Child = stack;
        return card;
    }

    private UIElement CreateViewportSection(string pluginId, string menuId, UiSection section)
    {
        var vpGrid = new Grid { RowSpacing = 10 };
        bool hasTitle = !string.IsNullOrWhiteSpace(section.Title);
        if (hasTitle)
        {
            vpGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var titleBlock = new TextBlock
            {
                Text = section.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(TextTitle)
            };
            vpGrid.Children.Add(titleBlock);
        }
        vpGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var vpBorder = new Border
        {
            Background = Brush(Color.FromArgb(255, 13, 13, 15)),
            CornerRadius = new CornerRadius(6),
            MinHeight = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _viewportController.AttachElement(pluginId, menuId, vpBorder);
        Grid.SetRow(vpBorder, hasTitle ? 1 : 0);
        vpGrid.Children.Add(vpBorder);
        return vpGrid;
    }

    /// <summary>紧凑工具栏：左侧状态文字，右侧按钮；放不下时按钮换到下一行。</summary>
    private UIElement CreateCompactToolbar(string pluginId, string menuId, UiSection section, Dictionary<string, JsonElement>? cachedValues)
    {
        var statusStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new FlowPanel { ColumnSpacing = 8, RowSpacing = 8 };

        foreach (var item in section.Items)
        {
            var initial = ResolveValue(item, cachedValues);
            if (item.Type == "status")
            {
                var tb = WrapText(FormatJsonValue(initial), 13, TextBody);
                _updaters[item.Key] = v => tb.Text = FormatJsonValue(v);
                statusStack.Children.Add(tb);
            }
            else if (item.Type == "button")
            {
                buttons.Children.Add(CreateButton(pluginId, menuId, item, initial));
            }
            else
            {
                statusStack.Children.Add(CreateItem(pluginId, menuId, item, initial));
            }
        }

        return new LabeledRow(statusStack, buttons) { LabelMinWidth = 160, MinRowHeight = 32 };
    }

    /// <summary>左右分栏：宽屏左右排列，窄屏上下堆叠；右栏 primary 按钮固定在底部。</summary>
    private UIElement CreateSplitLayout(string pluginId, string menuId, UiSection section, Dictionary<string, JsonElement>? cachedValues)
    {
        var leftStack = new StackPanel { Spacing = 12 };
        var rightTopStack = new StackPanel { Spacing = 14 };
        var rightBottomStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };

        foreach (var item in section.Items)
        {
            var el = CreateItem(pluginId, menuId, item, ResolveValue(item, cachedValues));
            if (string.Equals(item.Column, "left", StringComparison.OrdinalIgnoreCase))
            {
                leftStack.Children.Add(el);
            }
            else if (item.Primary)
            {
                rightBottomStack.Children.Add(el);
            }
            else
            {
                rightTopStack.Children.Add(el);
            }
        }

        var leftPane = new Border
        {
            Background = Brush(PaneBackground),
            BorderBrush = Brush(PaneBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = leftStack
        };

        var rightGrid = new Grid();
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(rightTopStack, 0);
        Grid.SetRow(rightBottomStack, 1);
        rightGrid.Children.Add(rightTopStack);
        rightGrid.Children.Add(rightBottomStack);

        return new ResponsiveSplit(leftPane, rightGrid);
    }

    // ---------------- 控件分发 ----------------

    private UIElement CreateItem(string pluginId, string menuId, UiItem item, JsonElement? initial) => item.Type switch
    {
        "table" => CreateTable(pluginId, menuId, item, initial),
        "chart" => CreateChart(item, initial),
        "source_picker" => CreateSourcePicker(pluginId, menuId, item, initial),
        "text" => CreateText(item, initial),
        "alert" => CreateAlert(item, initial),
        "divider" => new Border { Height = 1, Background = Brush(CardBorder), Margin = new Thickness(0, 2, 0, 2) },
        "image" => CreateImage(item, initial),
        "key_value" => CreateKeyValue(item, initial),
        "stats" => CreateStats(item, initial),
        "checkbox" => CreateCheckBox(pluginId, menuId, item, initial),
        "button" when item.Primary => CreateButton(pluginId, menuId, item, initial),
        _ => CreateOptionRow(pluginId, menuId, item, initial)
    };

    /// <summary>标签 + 控件的一行，宽度不足时自动上下排列。</summary>
    private UIElement CreateOptionRow(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        TextBlock? statusText = null;
        bool stretch = true;
        double flexible = 0;
        bool alwaysStacked = item.Stacked;

        FrameworkElement control;
        switch (item.Type)
        {
            case "switch":
                control = CreateSwitch(pluginId, menuId, item, initial);
                stretch = false;
                break;
            case "select":
                control = CreateSelect(pluginId, menuId, item, initial);
                break;
            case "segmented":
                control = CreateSegmented(pluginId, menuId, item, initial);
                break;
            case "input":
                control = CreateInput(pluginId, menuId, item, initial);
                break;
            case "password":
                control = CreatePassword(pluginId, menuId, item, initial);
                break;
            case "textarea":
                control = CreateTextArea(pluginId, menuId, item, initial);
                alwaysStacked = true;
                break;
            case "number":
                control = CreateNumber(pluginId, menuId, item, initial);
                break;
            case "slider":
                control = CreateSlider(pluginId, menuId, item, initial);
                break;
            case "radio":
                control = CreateRadio(pluginId, menuId, item, initial);
                break;
            case "checklist":
                control = CreateChecklist(pluginId, menuId, item, initial);
                alwaysStacked = true;
                break;
            case "button_group":
                control = CreateButtonGroup(pluginId, menuId, item);
                break;
            case "button":
                control = CreateButton(pluginId, menuId, item, initial);
                stretch = false;
                break;
            case "link":
                control = CreateLink(pluginId, menuId, item, initial);
                stretch = false;
                break;
            case "badge":
                control = CreateBadge(item, initial);
                stretch = false;
                break;
            case "progress":
                control = CreateProgress(item, initial);
                break;
            default:
                statusText = CreateStatusText(item, initial);
                control = statusText;
                flexible = 160;
                break;
        }

        var row = new LabeledRow(CreateLabel(item), control)
        {
            StretchControl = stretch,
            FlexibleMinWidth = flexible,
            AlwaysStacked = alwaysStacked
        };
        if (statusText is not null)
        {
            // 状态文字与标签同行时右对齐，换到下一行时左对齐
            row.InlineChanged += inline => statusText.TextAlignment = inline ? TextAlignment.Right : TextAlignment.Left;
        }
        return row;
    }

    private static UIElement CreateLabel(UiItem item)
    {
        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(item.Label))
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = 13.5,
                FontWeight = item.Stacked ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(TextNormal),
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (!string.IsNullOrWhiteSpace(item.Subtitle))
        {
            stack.Children.Add(WrapText(item.Subtitle!, 11.5, TextSecondary));
        }
        return stack;
    }

    // ---------------- 输入类控件 ----------------

    private ToggleSwitch CreateSwitch(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = initial?.ValueKind == JsonValueKind.True,
            OnContent = "开启",
            OffContent = "关闭",
            MinWidth = 0,
            IsEnabled = !item.Disabled
        };
        toggle.Toggled += async (_, _) =>
        {
            if (_suppressEvents) return;
            await RaiseAsync(pluginId, menuId, item.Key, "change", JsonSerializer.SerializeToElement(toggle.IsOn));
        };
        _updaters[item.Key] = v => toggle.IsOn = v.ValueKind == JsonValueKind.True;
        return toggle;
    }

    private ComboBox CreateSelect(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var combo = new ComboBox { MinWidth = 160, MaxWidth = item.Stacked ? double.PositiveInfinity : 380, IsEnabled = !item.Disabled };
        var options = item.Options ?? [];
        foreach (var opt in options)
        {
            combo.Items.Add(opt.Label);
        }

        void SelectByJson(JsonElement? val)
        {
            if (val is null) return;
            var targetRaw = val.Value.ToString();
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].Value.ToString(), targetRaw, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    break;
                }
            }
        }

        SelectByJson(initial);
        combo.SelectionChanged += async (_, _) =>
        {
            if (_suppressEvents || combo.SelectedIndex < 0) return;
            await RaiseAsync(pluginId, menuId, item.Key, "change", options[combo.SelectedIndex].Value.Clone());
        };
        _updaters[item.Key] = v => SelectByJson(v);
        return combo;
    }

    private TextBox CreateInput(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var tb = new TextBox
        {
            MinWidth = 220,
            Text = FormatJsonValue(initial),
            PlaceholderText = item.Placeholder ?? string.Empty,
            IsEnabled = !item.Disabled
        };
        tb.LostFocus += async (_, _) =>
        {
            if (_suppressEvents) return;
            await RaiseAsync(pluginId, menuId, item.Key, "change", JsonSerializer.SerializeToElement(tb.Text));
        };
        _updaters[item.Key] = v => tb.Text = FormatJsonValue(v);
        return tb;
    }

    private PasswordBox CreatePassword(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var pb = new PasswordBox
        {
            MinWidth = 220,
            Password = FormatJsonValue(initial),
            PlaceholderText = item.Placeholder ?? string.Empty,
            IsEnabled = !item.Disabled
        };
        pb.LostFocus += async (_, _) =>
        {
            if (_suppressEvents) return;
            await RaiseAsync(pluginId, menuId, item.Key, "change", JsonSerializer.SerializeToElement(pb.Password));
        };
        _updaters[item.Key] = v => pb.Password = FormatJsonValue(v);
        return pb;
    }

    private TextBox CreateTextArea(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var tb = new TextBox
        {
            Text = FormatJsonValue(initial),
            PlaceholderText = item.Placeholder ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 88,
            MaxHeight = 260,
            IsEnabled = !item.Disabled
        };
        ScrollViewer.SetVerticalScrollBarVisibility(tb, ScrollBarVisibility.Auto);
        tb.LostFocus += async (_, _) =>
        {
            if (_suppressEvents) return;
            await RaiseAsync(pluginId, menuId, item.Key, "change", JsonSerializer.SerializeToElement(tb.Text));
        };
        _updaters[item.Key] = v => tb.Text = FormatJsonValue(v);
        return tb;
    }

    private NumberBox CreateNumber(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        double val = initial?.ValueKind == JsonValueKind.Number && initial.Value.TryGetDouble(out var d) ? d : 0;
        double min = item.Min ?? 0;
        double max = item.Max ?? 1_000_000;
        var nb = new NumberBox
        {
            MinWidth = 160,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(val, min, max),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            IsEnabled = !item.Disabled
        };
        if (item.Step is > 0) nb.SmallChange = item.Step.Value;
        nb.ValueChanged += async (_, args) =>
        {
            if (_suppressEvents || double.IsNaN(args.NewValue)) return;
            await RaiseAsync(pluginId, menuId, item.Key, "change", JsonSerializer.SerializeToElement(args.NewValue));
        };
        _updaters[item.Key] = v =>
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var num)) nb.Value = num;
        };
        return nb;
    }

    private FrameworkElement CreateSlider(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        double min = item.Min ?? 0;
        double max = item.Max ?? 100;
        double step = item.Step is > 0 ? item.Step.Value : 1;
        double val = initial?.ValueKind == JsonValueKind.Number && initial.Value.TryGetDouble(out var d) ? d : min;

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            SmallChange = step,
            Value = Math.Clamp(val, min, max),
            MinWidth = 160,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !item.Disabled
        };
        var valueText = new TextBlock
        {
            MinWidth = 56,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(TextNormal),
            FontSize = 13
        };
        void UpdateText() => valueText.Text = $"{slider.Value:0.##}{(string.IsNullOrEmpty(item.Unit) ? "" : " " + item.Unit)}";
        UpdateText();

        // 拖动过程中只刷新数字，停下 250ms 后再通知插件，避免一次拖动产生大量事件
        var timer = DispatcherQueueTimerFor(TimeSpan.FromMilliseconds(250));
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await RaiseAsync(pluginId, menuId, item.Key, "change", JsonSerializer.SerializeToElement(slider.Value));
        };
        slider.ValueChanged += (_, _) =>
        {
            UpdateText();
            if (_suppressEvents) return;
            timer.Stop();
            timer.Start();
        };
        _updaters[item.Key] = v =>
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var num)) slider.Value = num;
        };

        var grid = new Grid { ColumnSpacing = 10, MinWidth = 240 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(slider);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    private UIElement CreateCheckBox(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var cb = new CheckBox
        {
            Content = WrapText(item.Label, 13.5, TextNormal),
            IsChecked = initial?.ValueKind == JsonValueKind.True,
            IsEnabled = !item.Disabled
        };
        cb.Click += async (_, _) =>
        {
            if (_suppressEvents) return;
            await RaiseAsync(pluginId, menuId, item.Key, "change", JsonSerializer.SerializeToElement(cb.IsChecked == true));
        };
        _updaters[item.Key] = v => cb.IsChecked = v.ValueKind == JsonValueKind.True;

        if (string.IsNullOrWhiteSpace(item.Subtitle)) return cb;
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(cb);
        var sub = WrapText(item.Subtitle!, 11.5, TextSecondary);
        sub.Margin = new Thickness(28, -4, 0, 0);
        stack.Children.Add(sub);
        return stack;
    }

    private FrameworkElement CreateRadio(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var options = item.Options ?? [];
        var panel = new FlowPanel { ColumnSpacing = 16, RowSpacing = 4 };
        string group = $"{item.Key}_{Guid.NewGuid():N}";
        var radios = new List<RadioButton>();
        // 设置初始选中项也会触发 Checked，控件加载完成前的事件一律忽略
        bool ready = false;
        panel.Loaded += (_, _) => ready = true;

        void Select(JsonElement? v)
        {
            string? raw = v?.ToString();
            for (int i = 0; i < options.Count; i++)
                radios[i].IsChecked = raw is not null && string.Equals(options[i].Value.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var opt in options)
        {
            var rb = new RadioButton { Content = opt.Label, GroupName = group, MinWidth = 0, IsEnabled = !item.Disabled };
            rb.Checked += async (_, _) =>
            {
                if (_suppressEvents || !ready) return;
                await RaiseAsync(pluginId, menuId, item.Key, "change", opt.Value.Clone());
            };
            radios.Add(rb);
            panel.Children.Add(rb);
        }
        Select(initial);
        _updaters[item.Key] = v => Select(v);
        return panel;
    }

    private FrameworkElement CreateChecklist(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var options = item.Options ?? [];
        var panel = new UniformWrapPanel { MinItemWidth = 150, ColumnSpacing = 12, RowSpacing = 2 };
        var boxes = new List<CheckBox>();

        void Apply(JsonElement? v)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (v is { ValueKind: JsonValueKind.Array } arr)
                foreach (var e in arr.EnumerateArray()) selected.Add(e.ToString());
            for (int i = 0; i < options.Count; i++) boxes[i].IsChecked = selected.Contains(options[i].Value.ToString());
        }

        foreach (var opt in options)
        {
            var cb = new CheckBox { Content = WrapText(opt.Label, 13, TextNormal), MinWidth = 0, IsEnabled = !item.Disabled };
            cb.Click += async (_, _) =>
            {
                if (_suppressEvents) return;
                var values = options.Where((_, i) => boxes[i].IsChecked == true).Select(o => o.Value.Clone()).ToList();
                await RaiseAsync(pluginId, menuId, item.Key, "change", JsonSerializer.SerializeToElement(values));
            };
            boxes.Add(cb);
            panel.Children.Add(cb);
        }
        Apply(initial);
        _updaters[item.Key] = v => Apply(v);
        return panel;
    }

    private FrameworkElement CreateSegmented(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var options = item.Options ?? [];
        // 每项至少 76px，放不下时自动折成多行，而不是被裁切
        var panel = new UniformWrapPanel { MinItemWidth = 76, ColumnSpacing = 8, RowSpacing = 8 };
        var buttons = new List<Button>(options.Count);

        void ApplyVisuals(JsonElement? selectedVal)
        {
            string? targetRaw = selectedVal?.ToString();
            for (int i = 0; i < options.Count; i++)
            {
                bool active = targetRaw is not null &&
                              string.Equals(options[i].Value.ToString(), targetRaw, StringComparison.OrdinalIgnoreCase);
                var b = buttons[i];
                b.Background = Brush(active ? Accent : ControlBackground);
                b.BorderBrush = Brush(active ? AccentBorder : ControlBorder);
                b.Foreground = Brush(active ? Colors.White : TextBody);
                b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        foreach (var opt in options)
        {
            var btn = new Button
            {
                Content = new TextBlock { Text = opt.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Height = 34,
                MinWidth = 0,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(6),
                FontSize = 13,
                BorderThickness = new Thickness(1),
                IsEnabled = !item.Disabled
            };
            ToolTipService.SetToolTip(btn, opt.Label);
            btn.Click += async (_, _) =>
            {
                if (_suppressEvents) return;
                ApplyVisuals(opt.Value);
                await RaiseAsync(pluginId, menuId, item.Key, "change", opt.Value.Clone());
            };
            panel.Children.Add(btn);
            buttons.Add(btn);
        }

        ApplyVisuals(initial);
        _updaters[item.Key] = v => ApplyVisuals(v);
        return panel;
    }

    // ---------------- 按钮与链接 ----------------

    private Button CreateButton(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var label = new TextBlock
        {
            Text = initial is { ValueKind: JsonValueKind.String } s ? s.GetString() : item.Label,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var btn = new Button { Content = label, IsEnabled = !item.Disabled, MinWidth = 0 };

        if (item.Primary)
        {
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            btn.Height = 40;
            btn.CornerRadius = new CornerRadius(8);
            btn.FontSize = 14;
            btn.FontWeight = FontWeights.SemiBold;
        }

        void ApplyButtonTheme(bool danger)
        {
            if (danger)
            {
                btn.Background = Brush(DangerColor);
                btn.Foreground = Brush(Colors.White);
            }
            else if (item.Primary)
            {
                btn.Background = Brush(Accent);
                btn.Foreground = Brush(Colors.White);
            }
            else
            {
                btn.ClearValue(Control.BackgroundProperty);
                btn.ClearValue(Control.ForegroundProperty);
            }
        }

        ApplyButtonTheme(item.Danger);

        btn.Click += async (_, _) => await RaiseAsync(pluginId, menuId, item.Key, "click", null);
        _updaters[item.Key] = v =>
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                label.Text = v.GetString() ?? item.Label;
            }
            else if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("label", out var lbl) && lbl.ValueKind == JsonValueKind.String)
                    label.Text = lbl.GetString() ?? item.Label;
                if (v.TryGetProperty("disabled", out var dis))
                    btn.IsEnabled = dis.ValueKind != JsonValueKind.True;
                if (v.TryGetProperty("danger", out var dng))
                    ApplyButtonTheme(dng.ValueKind == JsonValueKind.True);
            }
        };
        return btn;
    }

    private FrameworkElement CreateButtonGroup(string pluginId, string menuId, UiItem item)
    {
        var panel = new FlowPanel { ColumnSpacing = 8, RowSpacing = 8 };
        var buttons = new List<Button>();
        foreach (var opt in item.Options ?? [])
        {
            var btn = new Button
            {
                Content = new TextBlock { Text = opt.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                MinWidth = 0,
                IsEnabled = !item.Disabled
            };
            btn.Click += async (_, _) => await RaiseAsync(pluginId, menuId, item.Key, "click", opt.Value.Clone());
            buttons.Add(btn);
            panel.Children.Add(btn);
        }
        _updaters[item.Key] = v =>
        {
            // { "disabled": true } 或 { "disabled": ["value1", ...] }
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("disabled", out var dis)) return;
            var options = item.Options ?? [];
            for (int i = 0; i < buttons.Count; i++)
            {
                bool off = dis.ValueKind == JsonValueKind.True ||
                           (dis.ValueKind == JsonValueKind.Array && dis.EnumerateArray().Any(e => e.ToString() == options[i].Value.ToString()));
                buttons[i].IsEnabled = !off;
            }
        };
        return panel;
    }

    private FrameworkElement CreateLink(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        string text = initial is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? item.Url ?? "打开" : item.Url ?? "打开";
        var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
        var link = new HyperlinkButton { Content = label, Padding = new Thickness(0), MinWidth = 0, IsEnabled = !item.Disabled };
        if (!string.IsNullOrWhiteSpace(item.Url)) ToolTipService.SetToolTip(link, item.Url);
        link.Click += async (_, _) =>
        {
            // 只打开 http/https 地址，防止插件借此启动本地程序
            if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            await RaiseAsync(pluginId, menuId, item.Key, "click", null);
        };
        _updaters[item.Key] = v => label.Text = FormatJsonValue(v);
        return link;
    }

    // ---------------- 展示类控件 ----------------

    private TextBlock CreateStatusText(UiItem item, JsonElement? initial)
    {
        var tb = WrapText(FormatJsonValue(initial), 13.5, TextSecondary);
        tb.VerticalAlignment = VerticalAlignment.Center;
        _updaters[item.Key] = v => tb.Text = FormatJsonValue(v);
        return tb;
    }

    private UIElement CreateText(UiItem item, JsonElement? initial)
    {
        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(item.Label))
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(TextNormal),
                TextWrapping = TextWrapping.Wrap
            });
        }
        var body = WrapText(initial is null ? item.Subtitle ?? string.Empty : FormatJsonValue(initial), 13, TextSecondary);
        body.IsTextSelectionEnabled = true;
        body.LineHeight = 20;
        stack.Children.Add(body);
        _updaters[item.Key] = v => body.Text = FormatJsonValue(v);
        return stack;
    }

    private UIElement CreateAlert(UiItem item, JsonElement? initial)
    {
        var bar = new InfoBar
        {
            Title = item.Label,
            Message = initial is { ValueKind: JsonValueKind.String } s ? s.GetString() : item.Subtitle,
            Severity = ParseSeverity(item.Severity),
            IsOpen = true,
            IsClosable = false
        };
        void Apply(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                bar.Message = v.GetString();
                return;
            }
            if (v.ValueKind != JsonValueKind.Object) return;
            if (v.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) bar.Title = t.GetString();
            if (v.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) bar.Message = m.GetString();
            if (v.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String) bar.Severity = ParseSeverity(sev.GetString());
            if (v.TryGetProperty("visible", out var vis)) bar.IsOpen = vis.ValueKind != JsonValueKind.False;
        }
        if (initial is { ValueKind: JsonValueKind.Object } o) Apply(o);
        _updaters[item.Key] = Apply;
        return bar;
    }

    private FrameworkElement CreateBadge(UiItem item, JsonElement? initial)
    {
        var text = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = text
        };
        void Apply(string? label, string? severity)
        {
            var c = SeverityColor(severity);
            text.Text = label ?? string.Empty;
            text.Foreground = Brush(c);
            pill.Background = Brush(Color.FromArgb(40, c.R, c.G, c.B));
            pill.BorderBrush = Brush(Color.FromArgb(110, c.R, c.G, c.B));
        }
        void Update(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Object)
            {
                Apply(v.TryGetProperty("text", out var t) ? t.ToString() : text.Text,
                      v.TryGetProperty("severity", out var s) ? s.GetString() : item.Severity);
            }
            else
            {
                Apply(FormatJsonValue(v), item.Severity);
            }
        }
        if (initial is { } init) Update(init);
        else Apply(item.Placeholder ?? "-", item.Severity);
        _updaters[item.Key] = Update;
        return pill;
    }

    private FrameworkElement CreateProgress(UiItem item, JsonElement? initial)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = item.Max ?? 100, VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
        var text = new TextBlock { FontSize = 12.5, Foreground = Brush(TextSecondary), VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, TextAlignment = TextAlignment.Right };
        void Update(JsonElement? v)
        {
            double? value = null;
            string? label = null;
            if (v is { ValueKind: JsonValueKind.Number } n) value = n.GetDouble();
            else if (v is { ValueKind: JsonValueKind.Object } o)
            {
                if (o.TryGetProperty("value", out var pv) && pv.ValueKind == JsonValueKind.Number) value = pv.GetDouble();
                if (o.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String) label = pt.GetString();
            }
            bar.IsIndeterminate = value is null or < 0;
            if (value is >= 0) bar.Value = Math.Min(value.Value, bar.Maximum);
            text.Text = label ?? (value is >= 0 ? $"{value.Value / bar.Maximum * 100:0}%" : string.Empty);
        }
        Update(initial);
        _updaters[item.Key] = v => Update(v);

        var grid = new Grid { ColumnSpacing = 10, MinWidth = 220 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(bar);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private UIElement CreateImage(UiItem item, JsonElement? initial)
    {
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = item.Max ?? 240,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        async void Load(JsonElement? v)
        {
            if (v is not { ValueKind: JsonValueKind.String } s || string.IsNullOrWhiteSpace(s.GetString())) return;
            try
            {
                using var ms = new MemoryStream(Convert.FromBase64String(s.GetString()!));
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                image.Source = bmp;
            }
            catch
            {
            }
        }
        Load(initial);
        _updaters[item.Key] = v => Load(v);
        return new LabeledRow(CreateLabel(item), image) { AlwaysStacked = true, StretchControl = true };
    }

    private UIElement CreateKeyValue(UiItem item, JsonElement? initial)
    {
        var panel = new UniformWrapPanel { MinItemWidth = 200, ColumnSpacing = 16, RowSpacing = 12 };
        void Rebuild(JsonElement? v)
        {
            panel.Children.Clear();
            foreach (var (label, value, _, _) in ParseEntries(v))
            {
                var cell = new StackPanel { Spacing = 2 };
                cell.Children.Add(WrapText(label, 12, TextSecondary));
                var val = WrapText(value, 13.5, TextNormal);
                val.IsTextSelectionEnabled = true;
                cell.Children.Add(val);
                panel.Children.Add(cell);
            }
        }
        Rebuild(initial);
        _updaters[item.Key] = v => Rebuild(v);
        return WithOptionalHeader(item, panel);
    }

    private UIElement CreateStats(UiItem item, JsonElement? initial)
    {
        var panel = new UniformWrapPanel { MinItemWidth = 150, ColumnSpacing = 10, RowSpacing = 10 };
        void Rebuild(JsonElement? v)
        {
            panel.Children.Clear();
            foreach (var (label, value, sub, severity) in ParseEntries(v))
            {
                var cell = new StackPanel { Spacing = 4 };
                cell.Children.Add(WrapText(label, 12, TextSecondary));
                cell.Children.Add(new TextBlock
                {
                    Text = value,
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(severity is null ? TextTitle : SeverityColor(severity)),
                    TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrEmpty(sub)) cell.Children.Add(WrapText(sub, 11.5, TextMuted));
                panel.Children.Add(new Border
                {
                    Background = Brush(PaneBackground),
                    BorderBrush = Brush(PaneBorder),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = cell
                });
            }
        }
        Rebuild(initial);
        _updaters[item.Key] = v => Rebuild(v);
        return WithOptionalHeader(item, panel);
    }

    private static UIElement WithOptionalHeader(UiItem item, UIElement content)
    {
        if (string.IsNullOrWhiteSpace(item.Label) && string.IsNullOrWhiteSpace(item.Subtitle)) return content;
        return new LabeledRow(CreateLabel(item), content) { AlwaysStacked = true, StretchControl = true, StackSpacing = 8 };
    }

    private static IEnumerable<(string Label, string Value, string? Sub, string? Severity)> ParseEntries(JsonElement? v)
    {
        if (v is not { ValueKind: JsonValueKind.Array } arr) yield break;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            yield return (
                e.TryGetProperty("label", out var l) ? FormatJsonValue(l) : string.Empty,
                e.TryGetProperty("value", out var val) ? FormatJsonValue(val) : "-",
                e.TryGetProperty("sub", out var s) ? FormatJsonValue(s) : null,
                e.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String ? sev.GetString() : null);
        }
    }

    private FrameworkElement CreateChart(UiItem item, JsonElement? initial)
    {
        var chart = new TrafficChartView(item, initial);
        _updaters[item.Key] = v => chart.Update(v);
        return chart.Root;
    }

    // ---------------- 视觉源选择器 ----------------

    private UIElement CreateSourcePicker(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var root = new StackPanel { Spacing = 12 };

        string activeTab = string.IsNullOrWhiteSpace(item.ActiveTab) ? "window" : item.ActiveTab!;
        string? selectedId = initial is { ValueKind: JsonValueKind.String } sel ? sel.GetString() : null;
        var currentCards = item.Cards ?? [];

        var tabs = item.Tabs is { Count: > 0 }
            ? item.Tabs
            :
            [
                new UiSelectOption { Label = "程序", Value = JsonSerializer.SerializeToElement("window") },
                new UiSelectOption { Label = "屏幕", Value = JsonSerializer.SerializeToElement("fullscreen") }
            ];

        var tabPanel = new FlowPanel { ColumnSpacing = 18, RowSpacing = 4 };
        var tabVisuals = new List<(string Val, TextBlock Text, Border Indicator)>();

        void UpdateTabVisuals()
        {
            foreach (var (val, text, indicator) in tabVisuals)
            {
                bool active = string.Equals(val, activeTab, StringComparison.OrdinalIgnoreCase);
                text.Foreground = Brush(active ? Color.FromArgb(255, 249, 250, 251) : TextSecondary);
                text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        foreach (var t in tabs)
        {
            string tabVal = t.Value.ValueKind == JsonValueKind.String ? (t.Value.GetString() ?? "window") : t.Value.ToString();
            var tabStack = new StackPanel { Spacing = 5 };
            var tabText = new TextBlock { Text = t.Label, FontSize = 14 };
            var indicator = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Brush(Accent) };
            tabStack.Children.Add(tabText);
            tabStack.Children.Add(indicator);

            var tabBtn = new Button
            {
                Content = tabStack,
                Background = Brush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2)
            };
            tabBtn.Click += async (_, _) =>
            {
                if (_suppressEvents) return;
                activeTab = tabVal;
                UpdateTabVisuals();
                await RaiseAsync(pluginId, menuId, item.Key, "tab", t.Value.Clone());
            };
            tabVisuals.Add((tabVal, tabText, indicator));
            tabPanel.Children.Add(tabBtn);
        }
        UpdateTabVisuals();

        var refreshBtn = new Button { Content = "↻ 刷新", FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
        refreshBtn.Click += async (_, _) => await RaiseAsync(pluginId, menuId, item.Key, "refresh", null);
        root.Children.Add(new LabeledRow(tabPanel, refreshBtn) { LabelMinWidth = 100, MinRowHeight = 32, Spacing = 12 });

        // 卡片每张至少 150px，列数随宽度自动调整
        var cardPanel = new UniformWrapPanel { MinItemWidth = 150, ColumnSpacing = 12, RowSpacing = 12 };
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 370,
            MinHeight = 200,
            Content = cardPanel
        });

        var cardVisuals = new List<(string Id, Border CardBorder, TextBlock TitleText)>();

        void UpdateSelectionVisuals()
        {
            foreach (var (cid, border, titleTb) in cardVisuals)
            {
                bool isSel = string.Equals(cid, selectedId, StringComparison.OrdinalIgnoreCase);
                border.BorderBrush = Brush(isSel ? Accent : Color.FromArgb(255, 52, 52, 61));
                border.BorderThickness = new Thickness(isSel ? 2 : 1);
                border.Background = Brush(isSel ? Color.FromArgb(255, 30, 41, 59) : Color.FromArgb(255, 34, 34, 40));
                titleTb.Foreground = Brush(isSel ? Colors.White : TextBody);
            }
        }

        void RebuildCards(List<UiSourceCard> cards)
        {
            cardPanel.Children.Clear();
            cardVisuals.Clear();

            if (cards.Count == 0)
            {
                var empty = WrapText("暂无可捕获的窗口，请打开目标程序后点击右上角“刷新”", 13, TextMuted);
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.TextAlignment = TextAlignment.Center;
                empty.Margin = new Thickness(12, 60, 12, 60);
                cardPanel.MaxColumns = 1;
                cardPanel.Children.Add(empty);
                return;
            }
            cardPanel.MaxColumns = int.MaxValue;

            foreach (var card in cards)
            {
                var cardBorder = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6), BorderThickness = new Thickness(1) };
                ToolTipService.SetToolTip(cardBorder, string.IsNullOrWhiteSpace(card.Subtitle) ? card.Title : $"{card.Title}\n{card.Subtitle}");

                var cardStack = new StackPanel { Spacing = 6 };
                var thumbGrid = new Grid();
                var fallbackLabel = new TextBlock
                {
                    Text = card.Title,
                    FontSize = 11.5,
                    Foreground = Brush(TextMuted),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(8),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 2
                };
                var img = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                thumbGrid.Children.Add(fallbackLabel);
                thumbGrid.Children.Add(img);
                cardStack.Children.Add(new Border
                {
                    Height = 112,
                    CornerRadius = new CornerRadius(6),
                    Background = Brush(Color.FromArgb(255, 17, 17, 20)),
                    Child = thumbGrid
                });

                if (!string.IsNullOrWhiteSpace(card.ThumbnailBase64))
                {
                    _ = LoadThumbnailAsync(img, fallbackLabel, card.ThumbnailBase64!);
                }

                var titleTb = new TextBlock
                {
                    Text = card.Title,
                    FontSize = 12.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1
                };
                cardStack.Children.Add(titleTb);
                cardBorder.Child = cardStack;

                cardBorder.Tapped += async (_, _) =>
                {
                    if (_suppressEvents) return;
                    selectedId = card.Id;
                    UpdateSelectionVisuals();
                    await RaiseAsync(pluginId, menuId, item.Key, "select", JsonSerializer.SerializeToElement(card.Id));
                };

                cardPanel.Children.Add(cardBorder);
                cardVisuals.Add((card.Id, cardBorder, titleTb));
            }

            UpdateSelectionVisuals();
        }

        RebuildCards(currentCards);

        _updaters[item.Key] = v =>
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                selectedId = v.GetString();
                UpdateSelectionVisuals();
            }
            else if (v.ValueKind == JsonValueKind.Object)
            {
                var state = v.Deserialize<UiSourcePickerState>();
                if (state is null) return;
                if (!string.IsNullOrWhiteSpace(state.ActiveTab))
                {
                    activeTab = state.ActiveTab!;
                    UpdateTabVisuals();
                }
                if (state.SelectedId is not null) selectedId = state.SelectedId;
                if (state.Cards is not null) RebuildCards(state.Cards);
                else UpdateSelectionVisuals();
            }
        };

        return root;
    }

    private static async Task LoadThumbnailAsync(Image image, TextBlock fallbackLabel, string base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage { DecodePixelWidth = 288 };
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            image.Source = bmp;
            fallbackLabel.Visibility = Visibility.Collapsed;
        }
        catch
        {
        }
    }

    // ---------------- 表格 ----------------

    /// <summary>每列至少 96px；总宽度不够时横向滚动，单元格过长时省略并在悬停时显示全文。</summary>
    private UIElement CreateTable(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        const double minColumnWidth = 96;
        var columns = item.Columns ?? [];
        var actions = item.RowActions ?? [];
        // 操作列固定宽度：各行按钮数量不同（受 when_key 控制）时，数据列仍与表头对齐
        double actionsWidth = actions.Count > 0 ? actions.Count * 64 + (actions.Count - 1) * 6 : 0;
        double minTableWidth = columns.Count * minColumnWidth + actionsWidth + 20;
        var container = new StackPanel { Spacing = 6, Width = minTableWidth };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = container
        };
        // 横向滚动区会给内容无限宽度，表头和各行就会各自按内容分列而错位；这里固定为“可见宽度与最小宽度取大”
        scroller.SizeChanged += (_, e) => container.Width = Math.Max(minTableWidth, e.NewSize.Width);

        TextBlock Cell(string text, bool header)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = header ? 12 : 13,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(header ? TextSecondary : TextNormal),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };
            if (!header && text.Length > 8) ToolTipService.SetToolTip(tb, text);
            return tb;
        }

        void RebuildRows(List<Dictionary<string, JsonElement>>? rows)
        {
            container.Children.Clear();

            var header = new Grid
            {
                Padding = new Thickness(10, 6, 10, 6),
                Background = Brush(PaneBackground),
                CornerRadius = new CornerRadius(4)
            };
            for (int c = 0; c < columns.Count; c++)
            {
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var hText = Cell(columns[c].Label, true);
                Grid.SetColumn(hText, c);
                header.Children.Add(hText);
            }
            if (actions.Count > 0)
            {
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(actionsWidth) });
                var opText = Cell("操作", true);
                Grid.SetColumn(opText, columns.Count);
                header.Children.Add(opText);
            }
            container.Children.Add(header);

            if (rows is null || rows.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    // 未指定时沿用旧版默认文字，保持已发布插件的显示不变
                    Text = string.IsNullOrWhiteSpace(item.Placeholder) ? "暂无发现组网内同插件在线节点" : item.Placeholder,
                    FontSize = 13,
                    Margin = new Thickness(10, 8, 10, 4),
                    Foreground = Brush(TextMuted),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var row in rows)
            {
                var rowGrid = new Grid
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderBrush = Brush(Color.FromArgb(255, 42, 42, 48)),
                    BorderThickness = new Thickness(0, 0, 0, 1)
                };
                for (int c = 0; c < columns.Count; c++)
                {
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var cell = Cell(row.TryGetValue(columns[c].Key, out var cellVal) ? FormatJsonValue(cellVal) : "-", false);
                    Grid.SetColumn(cell, c);
                    rowGrid.Children.Add(cell);
                }

                if (actions.Count > 0)
                {
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(actionsWidth) });
                    var actPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    string? rowId = row.TryGetValue("row_id", out var rid) ? FormatJsonValue(rid) : null;

                    foreach (var act in actions)
                    {
                        if (!string.IsNullOrWhiteSpace(act.WhenKey) && act.WhenEquals is not null &&
                            (!row.TryGetValue(act.WhenKey, out var actual) ||
                             !string.Equals(actual.ToString(), act.WhenEquals.Value.ToString(), StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var btn = new Button
                        {
                            Content = new TextBlock { Text = act.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                            Padding = new Thickness(8, 4, 8, 4),
                            FontSize = 12,
                            MinWidth = 0,
                            MaxWidth = 64
                        };
                        ToolTipService.SetToolTip(btn, act.Label);
                        btn.Click += async (_, _) => await _onUiAction(pluginId, menuId, new EventUiActionParams
                        {
                            MenuId = menuId,
                            Key = item.Key,
                            Action = act.Id,
                            RowId = rowId
                        });
                        actPanel.Children.Add(btn);
                    }
                    Grid.SetColumn(actPanel, columns.Count);
                    rowGrid.Children.Add(actPanel);
                }
                container.Children.Add(rowGrid);
            }
        }

        List<Dictionary<string, JsonElement>>? initialRows = item.Rows;
        if (initial?.ValueKind == JsonValueKind.Array)
        {
            initialRows = initial.Value.Deserialize<List<Dictionary<string, JsonElement>>>();
        }
        RebuildRows(initialRows);

        _updaters[item.Key] = v =>
        {
            if (v.ValueKind == JsonValueKind.Array)
            {
                RebuildRows(v.Deserialize<List<Dictionary<string, JsonElement>>>());
            }
        };

        return WithOptionalTableHeader(item, scroller);
    }

    private static UIElement WithOptionalTableHeader(UiItem item, UIElement table) =>
        string.IsNullOrWhiteSpace(item.Subtitle)
            ? table
            : new LabeledRow(WrapText(item.Subtitle!, 12, TextSecondary), table) { AlwaysStacked = true, StretchControl = true };

    // ---------------- 工具方法 ----------------

    private Task RaiseAsync(string pluginId, string menuId, string key, string action, JsonElement? value) =>
        _onUiAction(pluginId, menuId, new EventUiActionParams
        {
            MenuId = menuId,
            Key = key,
            Action = action,
            Value = value
        });

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer DispatcherQueueTimerFor(TimeSpan interval)
    {
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        return timer;
    }

    private static InfoBarSeverity ParseSeverity(string? severity) => severity?.ToLowerInvariant() switch
    {
        "success" => InfoBarSeverity.Success,
        "warning" => InfoBarSeverity.Warning,
        "error" => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    private static Color SeverityColor(string? severity) => severity?.ToLowerInvariant() switch
    {
        "success" => Color.FromArgb(255, 16, 185, 129),
        "warning" => Color.FromArgb(255, 245, 158, 11),
        "error" => Color.FromArgb(255, 239, 68, 68),
        "info" => Accent,
        _ => TextSecondary
    };

    private static TextBlock WrapText(string text, double size, Color color) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush(color),
        TextWrapping = TextWrapping.Wrap
    };

    private static SolidColorBrush Brush(Color c) => new(c);

    private static JsonElement? ResolveValue(UiItem item, Dictionary<string, JsonElement>? cachedValues)
    {
        if (cachedValues is not null && cachedValues.TryGetValue(item.Key, out var cached))
        {
            return cached;
        }
        return item.Value;
    }

    private static string FormatJsonValue(JsonElement? el)
    {
        if (el is null) return string.Empty;
        return el.Value.ValueKind switch
        {
            JsonValueKind.String => el.Value.GetString() ?? string.Empty,
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => el.Value.ToString()
        };
    }
}
