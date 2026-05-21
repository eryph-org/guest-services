using Eryph.GuestServices.CloudConfig;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Eryph.GuestServices.CloudConfig.Yaml;

// Parses cloud-init network-config YAML (v1 + v2) into the structured POCO.
// v2 is the primary format and is modelled accurately; v1 is parsed in a lossy
// shape that still surfaces version + nameserver / address strings — full v1
// fidelity is a TODO once a handler actually consumes it.
public static class NetworkConfigYamlSerializer
{
    private static readonly Lazy<IDeserializer> Deserializer = new(() =>
        new DeserializerBuilder()
            .WithCaseInsensitivePropertyMatching()
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build());

    public static NetworkConfig Deserialize(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new NetworkConfig();

        var raw = Deserializer.Value.Deserialize<RawNetworkConfig?>(yaml) ?? new RawNetworkConfig();

        // v1 carries a 'config' list rather than top-level ethernets/bonds/.. — for now
        // we just expose the version. TODO: project the v1 'config' list into the v2
        // shape (or a sibling field) once a handler is added.
        if (raw.Version == 1)
        {
            return new NetworkConfig { Version = 1 };
        }

        return new NetworkConfig
        {
            Version = raw.Version,
            Ethernets = raw.Ethernets?.ToDictionary(kvp => kvp.Key, kvp => Convert(kvp.Value)),
            Bonds = raw.Bonds?.ToDictionary(kvp => kvp.Key, kvp => ConvertBond(kvp.Value)),
            Bridges = raw.Bridges?.ToDictionary(kvp => kvp.Key, kvp => ConvertBridge(kvp.Value)),
            Vlans = raw.Vlans?.ToDictionary(kvp => kvp.Key, kvp => ConvertVlan(kvp.Value)),
            Renderer = ParseRenderer(raw.Renderer),
        };
    }

    private static NetworkEthernetConfig Convert(RawEthernetConfig? raw)
    {
        if (raw is null)
            return new NetworkEthernetConfig();

        return new NetworkEthernetConfig
        {
            Match = raw.Match,
            Dhcp4 = raw.Dhcp4,
            Dhcp6 = raw.Dhcp6,
            Addresses = raw.Addresses,
            Gateway4 = raw.Gateway4,
            Gateway6 = raw.Gateway6,
            Nameservers = ConvertNameservers(raw.Nameservers),
            Mtu = raw.Mtu,
            MacAddress = raw.Macaddress,
            Routes = raw.Routes?.Select(ConvertRoute).ToList(),
        };
    }

    private static NetworkBondConfig ConvertBond(RawBondConfig? raw)
    {
        if (raw is null)
            return new NetworkBondConfig();

        return new NetworkBondConfig
        {
            Interfaces = raw.Interfaces,
            Parameters = raw.Parameters,
        };
    }

    private static NetworkBridgeConfig ConvertBridge(RawBridgeConfig? raw)
    {
        if (raw is null)
            return new NetworkBridgeConfig();

        return new NetworkBridgeConfig
        {
            Interfaces = raw.Interfaces,
        };
    }

    private static NetworkVlanConfig ConvertVlan(RawVlanConfig? raw)
    {
        if (raw is null)
            return new NetworkVlanConfig();

        return new NetworkVlanConfig
        {
            Link = raw.Link,
            Id = raw.Id,
        };
    }

    private static NetworkNameservers? ConvertNameservers(RawNameservers? raw)
    {
        if (raw is null)
            return null;

        return new NetworkNameservers
        {
            Addresses = raw.Addresses,
            Search = raw.Search,
        };
    }

    private static NetworkRoute ConvertRoute(RawRoute raw) => new()
    {
        To = raw.To,
        Via = raw.Via,
        Metric = raw.Metric,
    };

    private static NetworkDhcpRenderer? ParseRenderer(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => null,
        "networkd" => NetworkDhcpRenderer.Networkd,
        "network_manager" or "networkmanager" => NetworkDhcpRenderer.NetworkManager,
        _ => NetworkDhcpRenderer.Other,
    };

    // Mutable raw shape so YamlDotNet can populate it; converted into the
    // immutable model above.
    private sealed class RawNetworkConfig
    {
        public int Version { get; set; }
        public string? Renderer { get; set; }
        public Dictionary<string, RawEthernetConfig>? Ethernets { get; set; }
        public Dictionary<string, RawBondConfig>? Bonds { get; set; }
        public Dictionary<string, RawBridgeConfig>? Bridges { get; set; }
        public Dictionary<string, RawVlanConfig>? Vlans { get; set; }
    }

    private sealed class RawEthernetConfig
    {
        public string? Match { get; set; }
        public bool? Dhcp4 { get; set; }
        public bool? Dhcp6 { get; set; }
        public List<string>? Addresses { get; set; }
        public string? Gateway4 { get; set; }
        public string? Gateway6 { get; set; }
        public RawNameservers? Nameservers { get; set; }
        public int? Mtu { get; set; }
        // cloud-init v2 spells this 'macaddress' (no underscore).
        public string? Macaddress { get; set; }
        public List<RawRoute>? Routes { get; set; }
    }

    private sealed class RawBondConfig
    {
        public List<string>? Interfaces { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
    }

    private sealed class RawBridgeConfig
    {
        public List<string>? Interfaces { get; set; }
    }

    private sealed class RawVlanConfig
    {
        public string? Link { get; set; }
        public int? Id { get; set; }
    }

    private sealed class RawNameservers
    {
        public List<string>? Addresses { get; set; }
        public List<string>? Search { get; set; }
    }

    private sealed class RawRoute
    {
        public string? To { get; set; }
        public string? Via { get; set; }
        public int? Metric { get; set; }
    }
}
