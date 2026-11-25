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

    [JsonPropertyName("items")]
    public List<UiItem> Items { get; set; } = [];
}

public sealed class UiItem
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Supported: switch, select, input, number, status, button, table, viewport
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "status";

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

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
