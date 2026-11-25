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

public sealed class DeclarativePluginRenderer
{
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
            bool sectionHasViewport = section.Items.Any(i => i.Type == "viewport");
            var card = CreateSectionCard(pluginId, menuId, section, cachedValues, out bool containsViewport);

            if (containsViewport || sectionHasViewport)
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
            Background = new SolidColorBrush(Color.FromArgb(255, 34, 34, 38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 46, 53)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = section.Compact ? new Thickness(14, 10, 14, 10) : new Thickness(18, 16, 18, 16)
        };

        if (containsViewport)
        {
            var vpGrid = new Grid { RowSpacing = 10 };
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                vpGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                vpGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var titleBlock = new TextBlock
                {
                    Text = section.Title,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246))
                };
                Grid.SetRow(titleBlock, 0);
                vpGrid.Children.Add(titleBlock);
            }
            else
            {
                vpGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }

            var vpItem = section.Items.First(i => i.Type == "viewport");
            var vpBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 13, 13, 15)),
                CornerRadius = new CornerRadius(6),
                MinHeight = 360,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _viewportController.AttachElement(pluginId, menuId, vpBorder);
            Grid.SetRow(vpBorder, string.IsNullOrWhiteSpace(section.Title) ? 0 : 1);
            vpGrid.Children.Add(vpBorder);
            card.Child = vpGrid;
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
                Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 244, 246)),
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        if (section.Compact)
        {
            var bar = new Grid { ColumnSpacing = 10 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            foreach (var item in section.Items)
            {
                var initial = ResolveValue(item, cachedValues);
                if (item.Type == "status")
                {
                    var tb = new TextBlock
                    {
                        Text = FormatJsonValue(initial),
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 209, 213, 219))
                    };
                    _updaters[item.Key] = v => tb.Text = FormatJsonValue(v);
                    Grid.SetColumn(tb, 0);
                    bar.Children.Add(tb);
                }
                else if (item.Type == "button")
                {
                    rightButtons.Children.Add(CreateButton(pluginId, menuId, item, initial));
                }
            }
            Grid.SetColumn(rightButtons, 1);
            bar.Children.Add(rightButtons);
            stack.Children.Add(bar);
        }
        else if (string.Equals(section.Layout, "split", StringComparison.OrdinalIgnoreCase))
        {
            var splitGrid = new Grid { ColumnSpacing = 20 };
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star), MinWidth = 270 });

            var leftStack = new StackPanel { Spacing = 12 };
            var rightTopStack = new StackPanel { Spacing = 14 };
            var rightBottomStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };

            foreach (var item in section.Items)
            {
                var initial = ResolveValue(item, cachedValues);
                var el = item.Type == "table"
                    ? CreateTable(pluginId, menuId, item, initial)
                    : CreateOptionRow(pluginId, menuId, item, initial);

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
                Background = new SolidColorBrush(Color.FromArgb(255, 26, 26, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 42, 42, 50)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Child = leftStack
            };
            Grid.SetColumn(leftPane, 0);
            splitGrid.Children.Add(leftPane);

            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(rightTopStack, 0);
            Grid.SetRow(rightBottomStack, 1);
            rightGrid.Children.Add(rightTopStack);
            rightGrid.Children.Add(rightBottomStack);

            Grid.SetColumn(rightGrid, 1);
            splitGrid.Children.Add(rightGrid);

            stack.Children.Add(splitGrid);
        }
        else
        {
            for (int idx = 0; idx < section.Items.Count; idx++)
            {
                var item = section.Items[idx];
                var initial = ResolveValue(item, cachedValues);
                if (item.Type == "table")
                {
                    stack.Children.Add(CreateTable(pluginId, menuId, item, initial));
                }
                else
                {
                    stack.Children.Add(CreateOptionRow(pluginId, menuId, item, initial));
                }
            }
        }

        card.Child = stack;
        return card;
    }

    private UIElement CreateOptionRow(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        if (item.Type == "source_picker")
        {
            return CreateSourcePicker(pluginId, menuId, item, initial);
        }

        if (item.Primary && item.Type == "button")
        {
            return CreateButton(pluginId, menuId, item, initial);
        }

        FrameworkElement control = item.Type switch
        {
            "switch" => CreateSwitch(pluginId, menuId, item, initial),
            "select" => CreateSelect(pluginId, menuId, item, initial),
            "segmented" => CreateSegmented(pluginId, menuId, item, initial),
            "input" => CreateInput(pluginId, menuId, item, initial),
            "number" => CreateNumber(pluginId, menuId, item, initial),
            "button" => CreateButton(pluginId, menuId, item, initial),
            _ => CreateStatusText(item, initial)
        };

        if (item.Stacked)
        {
            var stacked = new StackPanel { Spacing = 6 };
            var header = new StackPanel { Spacing = 2 };
            header.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 231, 235))
            });
            if (!string.IsNullOrWhiteSpace(item.Subtitle))
            {
                header.Children.Add(new TextBlock
                {
                    Text = item.Subtitle,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175)),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            stacked.Children.Add(header);
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            stacked.Children.Add(control);
            return stacked;
        }

        var row = new Grid { MinHeight = 36, ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!string.IsNullOrWhiteSpace(item.Subtitle))
        {
            var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            labelStack.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = 13.5,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 231, 235))
            });
            labelStack.Children.Add(new TextBlock
            {
                Text = item.Subtitle,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175)),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(labelStack, 0);
            row.Children.Add(labelStack);
        }
        else
        {
            var label = new TextBlock
            {
                Text = item.Label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13.5,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 231, 235))
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);
        }

        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private ToggleSwitch CreateSwitch(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = initial?.ValueKind == JsonValueKind.True,
            OnContent = "开启",
            OffContent = "关闭",
            MinWidth = 0
        };
        toggle.Toggled += async (_, _) =>
        {
            if (_suppressEvents) return;
            await _onUiAction(pluginId, menuId, new EventUiActionParams
            {
                MenuId = menuId,
                Key = item.Key,
                Action = "change",
                Value = JsonSerializer.SerializeToElement(toggle.IsOn)
            });
        };
        _updaters[item.Key] = v => toggle.IsOn = v.ValueKind == JsonValueKind.True;
        return toggle;
    }

    private ComboBox CreateSelect(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var combo = item.Stacked
            ? new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }
            : new ComboBox { MinWidth = 180, MaxWidth = 380 };
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
            var opt = options[combo.SelectedIndex];
            await _onUiAction(pluginId, menuId, new EventUiActionParams
            {
                MenuId = menuId,
                Key = item.Key,
                Action = "change",
                Value = opt.Value.Clone()
            });
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
            PlaceholderText = item.Placeholder ?? string.Empty
        };
        tb.LostFocus += async (_, _) =>
        {
            if (_suppressEvents) return;
            await _onUiAction(pluginId, menuId, new EventUiActionParams
            {
                MenuId = menuId,
                Key = item.Key,
                Action = "change",
                Value = JsonSerializer.SerializeToElement(tb.Text)
            });
        };
        _updaters[item.Key] = v => tb.Text = FormatJsonValue(v);
        return tb;
    }

    private NumberBox CreateNumber(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        double val = 0;
        if (initial?.ValueKind == JsonValueKind.Number && initial.Value.TryGetDouble(out var d))
        {
            val = d;
        }
        double min = item.Min ?? 0;
        double max = item.Max ?? 1_000_000;
        double clamped = Math.Clamp(val, min, max);
        var nb = new NumberBox
        {
            MinWidth = 160,
            Minimum = min,
            Maximum = max,
            Value = clamped,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        nb.ValueChanged += async (_, args) =>
        {
            if (_suppressEvents || double.IsNaN(args.NewValue)) return;
            await _onUiAction(pluginId, menuId, new EventUiActionParams
            {
                MenuId = menuId,
                Key = item.Key,
                Action = "change",
                Value = JsonSerializer.SerializeToElement(args.NewValue)
            });
        };
        _updaters[item.Key] = v =>
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var num))
            {
                nb.Value = num;
            }
        };
        return nb;
    }

    private Grid CreateSegmented(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var options = item.Options ?? [];
        var grid = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        var buttons = new List<Button>(options.Count);

        void ApplyVisuals(JsonElement? selectedVal)
        {
            string? targetRaw = selectedVal?.ToString();
            for (int i = 0; i < options.Count; i++)
            {
                bool active = targetRaw is not null &&
                              string.Equals(options[i].Value.ToString(), targetRaw, StringComparison.OrdinalIgnoreCase);
                var b = buttons[i];
                b.Background = new SolidColorBrush(active ? Color.FromArgb(255, 59, 130, 246) : Color.FromArgb(255, 42, 42, 48));
                b.BorderBrush = new SolidColorBrush(active ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 58, 58, 68));
                b.Foreground = new SolidColorBrush(active ? Colors.White : Color.FromArgb(255, 209, 213, 219));
                b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        for (int i = 0; i < options.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var opt = options[i];
            var btn = new Button
            {
                Content = opt.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 34,
                CornerRadius = new CornerRadius(6),
                FontSize = 13,
                BorderThickness = new Thickness(1)
            };
            btn.Click += async (_, _) =>
            {
                if (_suppressEvents) return;
                ApplyVisuals(opt.Value);
                await _onUiAction(pluginId, menuId, new EventUiActionParams
                {
                    MenuId = menuId,
                    Key = item.Key,
                    Action = "change",
                    Value = opt.Value.Clone()
                });
            };
            Grid.SetColumn(btn, i);
            grid.Children.Add(btn);
            buttons.Add(btn);
        }

        ApplyVisuals(initial);
        _updaters[item.Key] = v => ApplyVisuals(v);
        return grid;
    }

    private UIElement CreateSourcePicker(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var root = new Grid { RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        string activeTab = string.IsNullOrWhiteSpace(item.ActiveTab) ? "window" : item.ActiveTab!;
        string? selectedId = initial is not null && initial.Value.ValueKind == JsonValueKind.String
            ? initial.Value.GetString()
            : null;
        var currentCards = item.Cards ?? [];

        var tabs = item.Tabs is { Count: > 0 }
            ? item.Tabs
            :
            [
                new UiSelectOption { Label = "程序", Value = JsonSerializer.SerializeToElement("window") },
                new UiSelectOption { Label = "屏幕", Value = JsonSerializer.SerializeToElement("fullscreen") }
            ];

        var headerBar = new Grid { ColumnSpacing = 12 };
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tabPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var tabVisuals = new List<(string Val, TextBlock Text, Border Indicator)>();

        void UpdateTabVisuals()
        {
            foreach (var (val, text, indicator) in tabVisuals)
            {
                bool active = string.Equals(val, activeTab, StringComparison.OrdinalIgnoreCase);
                text.Foreground = new SolidColorBrush(active ? Color.FromArgb(255, 249, 250, 251) : Color.FromArgb(255, 156, 163, 175));
                text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        foreach (var t in tabs)
        {
            string tabVal = t.Value.ValueKind == JsonValueKind.String ? (t.Value.GetString() ?? "window") : t.Value.ToString();
            var tabStack = new StackPanel { Spacing = 5 };
            var tabText = new TextBlock { Text = t.Label, FontSize = 14 };
            var indicator = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246))
            };
            tabStack.Children.Add(tabText);
            tabStack.Children.Add(indicator);

            var tabBtn = new Button
            {
                Content = tabStack,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2)
            };
            tabBtn.Click += async (_, _) =>
            {
                if (_suppressEvents) return;
                activeTab = tabVal;
                UpdateTabVisuals();
                await _onUiAction(pluginId, menuId, new EventUiActionParams
                {
                    MenuId = menuId,
                    Key = item.Key,
                    Action = "tab",
                    Value = t.Value.Clone()
                });
            };
            tabVisuals.Add((tabVal, tabText, indicator));
            tabPanel.Children.Add(tabBtn);
        }

        UpdateTabVisuals();
        Grid.SetColumn(tabPanel, 0);
        headerBar.Children.Add(tabPanel);

        var refreshBtn = new Button
        {
            Content = "↻ 刷新",
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4)
        };
        refreshBtn.Click += async (_, _) =>
        {
            await _onUiAction(pluginId, menuId, new EventUiActionParams
            {
                MenuId = menuId,
                Key = item.Key,
                Action = "refresh"
            });
        };
        Grid.SetColumn(refreshBtn, 1);
        headerBar.Children.Add(refreshBtn);

        Grid.SetRow(headerBar, 0);
        root.Children.Add(headerBar);

        var gridHost = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 370,
            MinHeight = 270,
            Content = gridHost
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        var cardVisuals = new List<(string Id, Border CardBorder, TextBlock TitleText)>();

        void UpdateSelectionVisuals()
        {
            foreach (var (cid, border, titleTb) in cardVisuals)
            {
                bool sel = string.Equals(cid, selectedId, StringComparison.OrdinalIgnoreCase);
                border.BorderBrush = new SolidColorBrush(sel ? Color.FromArgb(255, 59, 130, 246) : Color.FromArgb(255, 52, 52, 61));
                border.BorderThickness = new Thickness(sel ? 2 : 1);
                border.Background = new SolidColorBrush(sel ? Color.FromArgb(255, 30, 41, 59) : Color.FromArgb(255, 34, 34, 40));
                titleTb.Foreground = new SolidColorBrush(sel ? Colors.White : Color.FromArgb(255, 209, 213, 219));
            }
        }

        void RebuildCards(List<UiSourceCard> cards)
        {
            gridHost.Children.Clear();
            gridHost.RowDefinitions.Clear();
            gridHost.ColumnDefinitions.Clear();
            cardVisuals.Clear();

            if (cards.Count == 0)
            {
                gridHost.Children.Add(new TextBlock
                {
                    Text = "暂无可捕获的窗口，请打开目标程序后点击右上角“刷新”",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 60, 12, 60),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            gridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int rowCount = (cards.Count + 1) / 2;
            for (int r = 0; r < rowCount; r++)
            {
                gridHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                var cardBorder = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    BorderThickness = new Thickness(1)
                };

                var tooltipText = string.IsNullOrWhiteSpace(card.Subtitle) ? card.Title : $"{card.Title}\n{card.Subtitle}";
                ToolTipService.SetToolTip(cardBorder, tooltipText);

                var cardStack = new StackPanel { Spacing = 6 };
                var thumbBox = new Border
                {
                    Height = 112,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromArgb(255, 17, 17, 20))
                };

                var thumbGrid = new Grid();
                var fallbackLabel = new TextBlock
                {
                    Text = card.Title,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 2
                };
                var img = new Image
                {
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                thumbGrid.Children.Add(fallbackLabel);
                thumbGrid.Children.Add(img);
                thumbBox.Child = thumbGrid;

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

                cardStack.Children.Add(thumbBox);
                cardStack.Children.Add(titleTb);
                cardBorder.Child = cardStack;

                cardBorder.Tapped += async (_, _) =>
                {
                    if (_suppressEvents) return;
                    selectedId = card.Id;
                    UpdateSelectionVisuals();
                    await _onUiAction(pluginId, menuId, new EventUiActionParams
                    {
                        MenuId = menuId,
                        Key = item.Key,
                        Action = "select",
                        Value = JsonSerializer.SerializeToElement(card.Id)
                    });
                };

                Grid.SetRow(cardBorder, i / 2);
                Grid.SetColumn(cardBorder, i % 2);
                gridHost.Children.Add(cardBorder);
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
                if (state.SelectedId is not null)
                {
                    selectedId = state.SelectedId;
                }
                if (state.Cards is not null)
                {
                    RebuildCards(state.Cards);
                }
                else
                {
                    UpdateSelectionVisuals();
                }
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

    private Button CreateButton(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var btn = new Button
        {
            Content = initial is not null && initial.Value.ValueKind == JsonValueKind.String
                ? initial.Value.GetString()
                : item.Label,
            IsEnabled = !item.Disabled
        };

        if (item.Primary)
        {
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.Height = 40;
            btn.CornerRadius = new CornerRadius(8);
            btn.FontSize = 14;
            btn.FontWeight = FontWeights.SemiBold;
        }

        void ApplyButtonTheme(bool danger)
        {
            if (danger)
            {
                btn.Background = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38));
                btn.Foreground = new SolidColorBrush(Colors.White);
            }
            else if (item.Primary)
            {
                btn.Background = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));
                btn.Foreground = new SolidColorBrush(Colors.White);
            }
            else
            {
                btn.ClearValue(Control.BackgroundProperty);
                btn.ClearValue(Control.ForegroundProperty);
            }
        }

        ApplyButtonTheme(item.Danger);

        btn.Click += async (_, _) =>
        {
            await _onUiAction(pluginId, menuId, new EventUiActionParams
            {
                MenuId = menuId,
                Key = item.Key,
                Action = "click"
            });
        };
        _updaters[item.Key] = v =>
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                btn.Content = v.GetString() ?? item.Label;
            }
            else if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("label", out var lbl) && lbl.ValueKind == JsonValueKind.String)
                {
                    btn.Content = lbl.GetString() ?? item.Label;
                }
                if (v.TryGetProperty("disabled", out var dis))
                {
                    btn.IsEnabled = dis.ValueKind != JsonValueKind.True;
                }
                if (v.TryGetProperty("danger", out var dng))
                {
                    ApplyButtonTheme(dng.ValueKind == JsonValueKind.True);
                }
            }
        };
        return btn;
    }

    private TextBlock CreateStatusText(UiItem item, JsonElement? initial)
    {
        var tb = new TextBlock
        {
            Text = FormatJsonValue(initial),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
        };
        _updaters[item.Key] = v => tb.Text = FormatJsonValue(v);
        return tb;
    }

    private UIElement CreateTable(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var container = new StackPanel { Spacing = 6 };
        var columns = item.Columns ?? [];
        var actions = item.RowActions ?? [];

        void RebuildRows(List<Dictionary<string, JsonElement>>? rows)
        {
            container.Children.Clear();

            var header = new Grid
            {
                Padding = new Thickness(10, 6, 10, 6),
                Background = new SolidColorBrush(Color.FromArgb(255, 26, 26, 30)),
                CornerRadius = new CornerRadius(4)
            };
            for (int c = 0; c < columns.Count; c++)
            {
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var hText = new TextBlock
                {
                    Text = columns[c].Label,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
                };
                Grid.SetColumn(hText, c);
                header.Children.Add(hText);
            }
            if (actions.Count > 0)
            {
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var opText = new TextBlock
                {
                    Text = "操作",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 156, 163, 175))
                };
                Grid.SetColumn(opText, columns.Count);
                header.Children.Add(opText);
            }
            container.Children.Add(header);

            if (rows is null || rows.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "暂无发现组网内同插件在线节点",
                    FontSize = 13,
                    Margin = new Thickness(10, 8, 10, 4),
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 107, 114, 128))
                });
                return;
            }

            foreach (var row in rows)
            {
                var rowGrid = new Grid
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 42, 42, 48)),
                    BorderThickness = new Thickness(0, 0, 0, 1)
                };
                for (int c = 0; c < columns.Count; c++)
                {
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var cellText = row.TryGetValue(columns[c].Key, out var cellVal) ? FormatJsonValue(cellVal) : "-";
                    var tb = new TextBlock
                    {
                        Text = cellText,
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 231, 235))
                    };
                    Grid.SetColumn(tb, c);
                    rowGrid.Children.Add(tb);
                }

                if (actions.Count > 0)
                {
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var actPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    string? rowId = row.TryGetValue("row_id", out var rid) ? FormatJsonValue(rid) : null;

                    foreach (var act in actions)
                    {
                        if (!string.IsNullOrWhiteSpace(act.WhenKey) && act.WhenEquals is not null)
                        {
                            if (!row.TryGetValue(act.WhenKey, out var actual) ||
                                !string.Equals(actual.ToString(), act.WhenEquals.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        var btn = new Button
                        {
                            Content = act.Label,
                            Padding = new Thickness(10, 4, 10, 4),
                            FontSize = 12
                        };
                        btn.Click += async (_, _) =>
                        {
                            await _onUiAction(pluginId, menuId, new EventUiActionParams
                            {
                                MenuId = menuId,
                                Key = item.Key,
                                Action = act.Id,
                                RowId = rowId
                            });
                        };
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
                var updated = v.Deserialize<List<Dictionary<string, JsonElement>>>();
                RebuildRows(updated);
            }
        };

        return container;
    }

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
