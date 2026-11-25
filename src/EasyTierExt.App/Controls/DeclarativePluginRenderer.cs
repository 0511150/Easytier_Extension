using System.Text.Json;
using EasyTierExt.App.Native;
using EasyTierExt.Contracts.Ipc;
using EasyTierExt.Contracts.Models;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        var row = new Grid { MinHeight = 36, ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = item.Label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 229, 231, 235))
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        FrameworkElement control = item.Type switch
        {
            "switch" => CreateSwitch(pluginId, menuId, item, initial),
            "select" => CreateSelect(pluginId, menuId, item, initial),
            "input" => CreateInput(pluginId, menuId, item, initial),
            "number" => CreateNumber(pluginId, menuId, item, initial),
            "button" => CreateButton(pluginId, menuId, item, initial),
            _ => CreateStatusText(item, initial)
        };

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
        var combo = new ComboBox { MinWidth = 180, MaxWidth = 380 };
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

    private Button CreateButton(string pluginId, string menuId, UiItem item, JsonElement? initial)
    {
        var btn = new Button
        {
            Content = initial is not null && initial.Value.ValueKind == JsonValueKind.String
                ? initial.Value.GetString()
                : item.Label,
            IsEnabled = !item.Disabled
        };
        if (item.Danger)
        {
            btn.Background = new SolidColorBrush(Color.FromArgb(255, 185, 28, 28));
            btn.Foreground = new SolidColorBrush(Colors.White);
        }
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
