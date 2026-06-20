using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml.Converters;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.NodeDeserializers;

namespace Eryph.GuestServices.CloudConfig.Yaml;

// Parses cloud-init network-config YAML (v1 + v2) into the structured POCO.
// v2 is the primary format and is modelled accurately; v1 entries are projected
// into the same v2-shape (Ethernets keyed by interface name) so downstream
// handlers can treat both schemas uniformly. Only the v1 subset that the
// Windows applier supports is projected today — bonds/bridges/vlans are
// preserved as Version=1 but not flattened.
public static class NetworkConfigYamlSerializer
{
    private static readonly Lazy<IDeserializer> Deserializer = new(() =>
        new DeserializerBuilder()
            .WithCaseInsensitivePropertyMatching()
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            // The v2 `addresses:` list accepts both plain scalars and the
            // advanced single-key map form; this converter keeps the address
            // and never throws on the map shape (issue #59 failure class).
            .WithAttributeOverride<RawEthernetConfig>(
                e => e.Addresses!,
                new YamlConverterAttribute(typeof(NetworkAddressListYamlConverter)))
            // PyYAML SafeLoader-equivalent YAML 1.1 scalar resolution. The
            // network-config schema has int? fields (mtu, vlan id, route
            // metric) where leading-zero octal / underscore forms must parse
            // the same way cloud-init's safe_load would.
            .WithNodeDeserializer(
                new Yaml11ScalarResolver(),
                s => s.Before<ScalarNodeDeserializer>())
            // Registered so YamlDotNet can resolve the converter attached via
            // WithAttributeOverride above (it reports Accepts(_) => false).
            .WithTypeConverter(new NetworkAddressListYamlConverter())
            .Build());

    public static NetworkConfig Deserialize(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new NetworkConfig();

        // Strip a leading UTF-8 BOM if a producer (often a Windows editor, and
        // this is Windows-side tooling) emitted one. The StringReader path below
        // does not skip it and the parser would choke on U+FEFF before the
        // document. Mirrors NoCloudDataSource.DecodeUtf8.
        if (yaml[0] == '\uFEFF')
            yaml = yaml[1..];

        // Wrap in a MergingParser so YAML 1.1 merge keys (`<<: *anchor`) are
        // expanded — netplan/network-config uses anchors to share common
        // interface settings, matching PyYAML SafeLoader.
        var parser = new MergingParser(new Parser(new StringReader(yaml)));
        var root = Deserializer.Value.Deserialize<RawNetworkRoot?>(parser) ?? new RawNetworkRoot();

        // netplan and many cloud-init samples wrap the whole document under a
        // top-level `network:` key (the form every /etc/netplan file uses);
        // the bare form (version/ethernets at the root) is equally valid. When
        // the wrapper is present it carries the real config — unwrap it so both
        // forms parse identically instead of the wrapped one yielding an empty
        // result (the issue #59 silent-failure class).
        var raw = root.Network ?? root;

        // v1 carries a 'config' list rather than top-level ethernets/bonds/.. —
        // project the physical entries to the v2-shape Ethernets dictionary so
        // a single applier can serve both schemas. Non-physical entries (vlan,
        // bond, bridge, nameserver) are deferred.
        if (raw.Version == 1)
        {
            return ConvertV1(raw);
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

    private static NetworkConfig ConvertV1(RawNetworkConfig raw)
    {
        if (raw.Config is null || raw.Config.Count == 0)
            return new NetworkConfig { Version = 1 };

        // v1 standalone "nameserver" entries supply global DNS that applies to
        // all physical interfaces (cloud-init's behavior). Collect them first
        // so each projected ethernet inherits them when its own subnet block
        // doesn't provide more specific DNS.
        var globalDnsAddresses = new List<string>();
        var globalDnsSearch = new List<string>();
        foreach (var entry in raw.Config)
        {
            if (string.Equals(entry.Type, "nameserver", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Address is { Count: > 0 })
                    globalDnsAddresses.AddRange(entry.Address);
                if (entry.Search is { Count: > 0 })
                    globalDnsSearch.AddRange(entry.Search);
            }
        }

        var ethernets = new Dictionary<string, NetworkEthernetConfig>(StringComparer.Ordinal);
        foreach (var entry in raw.Config)
        {
            if (!string.Equals(entry.Type, "physical", StringComparison.OrdinalIgnoreCase))
                continue;

            // Without a stable handle (name) we cannot key the adapter; skip.
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            ethernets[entry.Name] = ProjectV1Physical(entry, globalDnsAddresses, globalDnsSearch);
        }

        return new NetworkConfig
        {
            Version = 1,
            Ethernets = ethernets.Count == 0 ? null : ethernets,
        };
    }

    private static NetworkEthernetConfig ProjectV1Physical(
        RawV1ConfigEntry entry,
        IReadOnlyList<string> globalDnsAddresses,
        IReadOnlyList<string> globalDnsSearch)
    {
        bool? dhcp4 = null;
        var addresses = new List<string>();
        string? gateway4 = null;
        var dnsAddresses = new List<string>();
        var dnsSearch = new List<string>();
        var routes = new List<NetworkRoute>();

        if (entry.Subnets is { Count: > 0 })
        {
            foreach (var subnet in entry.Subnets)
            {
                // cloud-init v1 subnet types: dhcp / dhcp4 / dhcp6 / static / static6 / manual.
                // We only project the v4 forms today; v6 follows the same shape if
                // someone adds it later.
                var subnetType = (subnet.Type ?? string.Empty).Trim().ToLowerInvariant();
                if (subnetType is "dhcp" or "dhcp4")
                {
                    dhcp4 = true;
                }
                else if (subnetType is "static" or "static4")
                {
                    if (!string.IsNullOrWhiteSpace(subnet.Address))
                        addresses.Add(subnet.Address);
                    if (!string.IsNullOrWhiteSpace(subnet.Gateway))
                        gateway4 ??= subnet.Gateway;
                }

                if (subnet.DnsNameservers is { Count: > 0 })
                    dnsAddresses.AddRange(subnet.DnsNameservers);
                if (subnet.DnsSearch is { Count: > 0 })
                    dnsSearch.AddRange(subnet.DnsSearch);

                if (subnet.Routes is { Count: > 0 })
                {
                    foreach (var r in subnet.Routes)
                        routes.Add(new NetworkRoute { To = r.Network, Via = r.Gateway, Metric = r.Metric });
                }
            }
        }
        else
        {
            // No subnets block at all -> default cloud-init behaviour is DHCP.
            dhcp4 = true;
        }

        // Inherit global DNS only when the subnet block did not specify any.
        if (dnsAddresses.Count == 0 && globalDnsAddresses.Count > 0)
            dnsAddresses.AddRange(globalDnsAddresses);
        if (dnsSearch.Count == 0 && globalDnsSearch.Count > 0)
            dnsSearch.AddRange(globalDnsSearch);

        NetworkNameservers? nameservers = null;
        if (dnsAddresses.Count > 0 || dnsSearch.Count > 0)
        {
            nameservers = new NetworkNameservers
            {
                Addresses = dnsAddresses.Count == 0 ? null : dnsAddresses,
                Search = dnsSearch.Count == 0 ? null : dnsSearch,
            };
        }

        return new NetworkEthernetConfig
        {
            Dhcp4 = dhcp4,
            Addresses = addresses.Count == 0 ? null : addresses,
            Gateway4 = gateway4,
            Nameservers = nameservers,
            Mtu = entry.Mtu,
            MacAddress = entry.MacAddress,
            Routes = routes.Count == 0 ? null : routes,
        };
    }

    private static NetworkEthernetConfig Convert(RawEthernetConfig? raw)
    {
        if (raw is null)
            return new NetworkEthernetConfig();

        return new NetworkEthernetConfig
        {
            Match = ConvertMatch(raw.Match),
            Dhcp4 = raw.Dhcp4,
            Dhcp6 = raw.Dhcp6,
            Addresses = raw.Addresses,
            Gateway4 = raw.Gateway4,
            Gateway6 = raw.Gateway6,
            Nameservers = ConvertNameservers(raw.Nameservers),
            Mtu = raw.Mtu,
            MacAddress = raw.MacAddress,
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

    private static NetworkMatch? ConvertMatch(RawMatch? raw)
    {
        // Drop an empty/all-null match block so downstream "has a selector?"
        // checks stay a simple null test.
        if (raw is null || (raw.Name is null && raw.MacAddress is null && raw.Driver is null))
            return null;

        return new NetworkMatch
        {
            Name = raw.Name,
            MacAddress = raw.MacAddress,
            Driver = raw.Driver,
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

    // Root shape that also accepts the `network:`-wrapped form. When the wrapper
    // is present, YamlDotNet populates Network and leaves the inherited
    // top-level fields at their defaults; the bare form populates the inherited
    // fields and leaves Network null. Deserialize() picks whichever carries the
    // config.
    private sealed class RawNetworkRoot : RawNetworkConfig
    {
        public RawNetworkConfig? Network { get; set; }
    }

    // Mutable raw shape so YamlDotNet can populate it; converted into the
    // immutable model above.
    private class RawNetworkConfig
    {
        public int Version { get; set; }
        public string? Renderer { get; set; }
        public Dictionary<string, RawEthernetConfig>? Ethernets { get; set; }
        public Dictionary<string, RawBondConfig>? Bonds { get; set; }
        public Dictionary<string, RawBridgeConfig>? Bridges { get; set; }
        public Dictionary<string, RawVlanConfig>? Vlans { get; set; }
        // v1 only: top-level 'config' list of typed entries.
        public List<RawV1ConfigEntry>? Config { get; set; }
    }

    // v1 'config' list entry. The schema is a discriminated union via `type`;
    // we only consume the fields relevant to physical adapters and standalone
    // nameserver records today.
    private sealed class RawV1ConfigEntry
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        // v1 spells it 'mac_address' (with underscore); the underscored naming
        // convention maps MacAddress -> mac_address automatically.
        public string? MacAddress { get; set; }
        public int? Mtu { get; set; }
        public List<RawV1Subnet>? Subnets { get; set; }
        // For type=nameserver entries.
        public List<string>? Address { get; set; }
        public List<string>? Search { get; set; }
    }

    private sealed class RawV1Subnet
    {
        public string? Type { get; set; }
        public string? Address { get; set; }
        public string? Gateway { get; set; }
        public List<string>? DnsNameservers { get; set; }
        public List<string>? DnsSearch { get; set; }
        public List<RawV1Route>? Routes { get; set; }
    }

    private sealed class RawV1Route
    {
        public string? Network { get; set; }
        public string? Gateway { get; set; }
        public int? Metric { get; set; }
    }

    private sealed class RawEthernetConfig
    {
        public RawMatch? Match { get; set; }
        public bool? Dhcp4 { get; set; }
        public bool? Dhcp6 { get; set; }
        public List<string>? Addresses { get; set; }
        public string? Gateway4 { get; set; }
        public string? Gateway6 { get; set; }
        public RawNameservers? Nameservers { get; set; }
        public int? Mtu { get; set; }
        // cloud-init v2 spells this 'macaddress' (no underscore). The
        // explicit alias makes the intent locatable so a future C# rename
        // can't silently break v2 parsing — relying on the field name
        // misspelling to defeat UnderscoredNamingConvention was fragile.
        [YamlMember(Alias = "macaddress", ApplyNamingConventions = false)]
        public string? MacAddress { get; set; }
        public List<RawRoute>? Routes { get; set; }
    }

    // cloud-init v2 'match' sub-map. Modelled as an object (not a string) so a
    // `match: {macaddress: ..}` block parses instead of throwing — the bug
    // behind issue #59, where the swallowed parse failure nulled the whole
    // network-config.
    private sealed class RawMatch
    {
        public string? Name { get; set; }
        // netplan spells it 'macaddress' (no underscore), same as the
        // top-level ethernet field; pin the alias so the underscored naming
        // convention can't rewrite it to 'mac_address'.
        [YamlMember(Alias = "macaddress", ApplyNamingConventions = false)]
        public string? MacAddress { get; set; }
        public string? Driver { get; set; }
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
