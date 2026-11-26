using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyTierExt.Contracts.Models;

public sealed class UiPageSchema
{
    [JsonPropertyName("sections")]
    public List<UiSection> Sections { get; set; } = [];
}

public sealed class UiSection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("compact")]
    public bool Compact { get; set; }

    [JsonPropertyName("layout")]
    public string? Layout { get; set; }

    [JsonPropertyName("items")]
    public List<UiItem> Items { get; set; } = [];
}

public sealed class UiItem
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Supported: switch, select, segmented, source_picker, input, number, status, button, table, chart, viewport,
    /// text, alert, badge, divider, progress, slider, checkbox, radio, checklist, textarea, password, link, image,
    /// key_value, stats, button_group
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "status";

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("column")]
    public string? Column { get; set; }

    [JsonPropertyName("stacked")]
    public bool Stacked { get; set; }

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("active_tab")]
    public string? ActiveTab { get; set; }

    [JsonPropertyName("tabs")]
    public List<UiSelectOption>? Tabs { get; set; }

    [JsonPropertyName("cards")]
    public List<UiSourceCard>? Cards { get; set; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("placeholder")]
    public string? Placeholder { get; set; }

    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    /// <summary>slider 的步进值。</summary>
    [JsonPropertyName("step")]
    public double? Step { get; set; }

    /// <summary>alert / badge 的样式：info、success、warning、error。</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    /// <summary>link 点击时用系统浏览器打开的地址（仅限 http/https）。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("danger")]
    public bool Danger { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("options")]
    public List<UiSelectOption>? Options { get; set; }

    [JsonPropertyName("columns")]
    public List<UiTableColumn>? Columns { get; set; }

    [JsonPropertyName("row_actions")]
    public List<UiRowAction>? RowActions { get; set; }

    [JsonPropertyName("rows")]
    public List<Dictionary<string, JsonElement>>? Rows { get; set; }
}

public sealed class UiSelectOption
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

public sealed class UiTableColumn
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

public sealed class UiRowAction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("when_key")]
    public string? WhenKey { get; set; }

    [JsonPropertyName("when_equals")]
    public JsonElement? WhenEquals { get; set; }
}

public sealed class UiSourceCard
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("thumbnail_base64")]
    public string? ThumbnailBase64 { get; set; }
}

public sealed class UiSourcePickerState
{
    [JsonPropertyName("active_tab")]
    public string? ActiveTab { get; set; }

    [JsonPropertyName("selected_id")]
    public string? SelectedId { get; set; }

    [JsonPropertyName("cards")]
    public List<UiSourceCard>? Cards { get; set; }
}
