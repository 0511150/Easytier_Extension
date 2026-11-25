using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyTierExt.Contracts.Models;

public sealed class HostNetworkStatus
{
    [JsonPropertyName("network_id")]
    public string NetworkId { get; set; } = string.Empty;

    [JsonPropertyName("network_name")]
    public string NetworkName { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("ipv4")]
    public string Ipv4 { get; set; } = string.Empty;

    [JsonPropertyName("cidr")]
    public string Cidr { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("rx_bytes")]
    public long RxBytes { get; set; }

    [JsonPropertyName("tx_bytes")]
    public long TxBytes { get; set; }

    [JsonPropertyName("uptime_sec")]
    public long UptimeSec { get; set; }

    [JsonPropertyName("peer_count")]
    public int PeerCount { get; set; }
}

public sealed class HostPeerInfo
{
    [JsonPropertyName("network_id")]
    public string NetworkId { get; set; } = string.Empty;

    [JsonPropertyName("network_name")]
    public string NetworkName { get; set; } = string.Empty;

    [JsonPropertyName("peer_id")]
    public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("ipv4")]
    public string Ipv4 { get; set; } = string.Empty;

    [JsonPropertyName("route_type")]
    public string RouteType { get; set; } = "relay";

    [JsonPropertyName("latency_ms")]
    public string LatencyMs { get; set; } = "-";

    [JsonPropertyName("loss_rate")]
    public string LossRate { get; set; } = "-";

    [JsonPropertyName("nat_type")]
    public string NatType { get; set; } = "-";

    [JsonPropertyName("tunnel_proto")]
    public string TunnelProto { get; set; } = "-";

    [JsonPropertyName("rx_bytes")]
    public string RxBytes { get; set; } = "-";

    [JsonPropertyName("tx_bytes")]
    public string TxBytes { get; set; } = "-";

    [JsonPropertyName("is_self")]
    public bool IsSelf { get; set; }
}

public sealed class ServicePeerInfo
{
    [JsonPropertyName("network_id")]
    public string NetworkId { get; set; } = string.Empty;

    [JsonPropertyName("network_name")]
    public string NetworkName { get; set; } = string.Empty;

    [JsonPropertyName("ipv4")]
    public string Ipv4 { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("route_type")]
    public string RouteType { get; set; } = "relay";

    [JsonPropertyName("latency_ms")]
    public string LatencyMs { get; set; } = "-";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("proto")]
    public string Proto { get; set; } = "tcp";

    [JsonPropertyName("meta")]
    public JsonElement Meta { get; set; }
}
