using System.Text.Json;
using System.Text.Json.Serialization;
using EasyTierExt.Contracts.Models;

namespace EasyTierExt.Contracts.Ipc;

public static class IpcJsonContext
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class PluginHelloParams
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public sealed class PluginHelloResult
{
    [JsonPropertyName("host_version")]
    public string HostVersion { get; set; } = "1.0.0";
}

public sealed class GetPeersParams
{
    [JsonPropertyName("network_id")]
    public string? NetworkId { get; set; }
}

public sealed class DiscoveryRegisterParams
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("proto")]
    public string Proto { get; set; } = "tcp";

    [JsonPropertyName("meta")]
    public JsonElement Meta { get; set; }

    [JsonPropertyName("network_ids")]
    public List<string>? NetworkIds { get; set; }
}

public sealed class DiscoveryUpdateMetaParams
{
    [JsonPropertyName("meta")]
    public JsonElement Meta { get; set; }

    [JsonPropertyName("network_ids")]
    public List<string>? NetworkIds { get; set; }
}

public sealed class UiSetSchemaParams
{
    [JsonPropertyName("menu_id")]
    public string MenuId { get; set; } = string.Empty;

    [JsonPropertyName("schema")]
    public UiPageSchema Schema { get; set; } = new();
}

public sealed class UiUpdateValuesParams
{
    [JsonPropertyName("menu_id")]
    public string MenuId { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public Dictionary<string, JsonElement> Values { get; set; } = [];
}

public sealed class UiNavigateToParams
{
    [JsonPropertyName("menu_id")]
    public string MenuId { get; set; } = string.Empty;
}

public sealed class UiGetViewportHostParams
{
    [JsonPropertyName("menu_id")]
    public string MenuId { get; set; } = string.Empty;
}

public sealed class UiGetViewportHostResult
{
    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public sealed class EventNetworksChangedParams
{
    [JsonPropertyName("networks")]
    public List<HostNetworkStatus> Networks { get; set; } = [];
}

public sealed class EventPeersUpdatedParams
{
    [JsonPropertyName("network_id")]
    public string NetworkId { get; set; } = string.Empty;

    [JsonPropertyName("peers")]
    public List<HostPeerInfo> Peers { get; set; } = [];
}

public sealed class EventDiscoveryUpdatedParams
{
    [JsonPropertyName("service_peers")]
    public List<ServicePeerInfo> ServicePeers { get; set; } = [];
}

public sealed class EventUiActionParams
{
    [JsonPropertyName("menu_id")]
    public string MenuId { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("row_id")]
    public string? RowId { get; set; }
}

public sealed class EventPageOpenedParams
{
    [JsonPropertyName("menu_id")]
    public string MenuId { get; set; } = string.Empty;
}

public sealed class EventViewportChangedParams
{
    [JsonPropertyName("menu_id")]
    public string MenuId { get; set; } = string.Empty;

    [JsonPropertyName("visible")]
    public bool Visible { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}
