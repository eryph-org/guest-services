namespace Eryph.GuestServices.CloudConfig;

// Cloud-init network-config — primarily models v2 (netplan-style), with v1 supported
// in a lossy form via the same POCO.
// Reference: https://cloudinit.readthedocs.io/en/latest/reference/network-config-format-v2.html
public sealed record NetworkConfig
{
    public int Version { get; init; }

    public IReadOnlyDictionary<string, NetworkEthernetConfig>? Ethernets { get; init; }

    public IReadOnlyDictionary<string, NetworkBondConfig>? Bonds { get; init; }

    public IReadOnlyDictionary<string, NetworkBridgeConfig>? Bridges { get; init; }

    public IReadOnlyDictionary<string, NetworkVlanConfig>? Vlans { get; init; }

    // Informational only on Windows — kept so handlers can surface it for diagnostics.
    public NetworkDhcpRenderer? Renderer { get; init; }
}

public sealed record NetworkEthernetConfig
{
    // The cloud-init v2 'match' clause is a sub-object (name / macaddress / driver).
    // For v1 we keep it as a free-form string. A richer model can land later if needed.
    public string? Match { get; init; }

    public bool? Dhcp4 { get; init; }

    public bool? Dhcp6 { get; init; }

    public IReadOnlyList<string>? Addresses { get; init; }

    public string? Gateway4 { get; init; }

    public string? Gateway6 { get; init; }

    public NetworkNameservers? Nameservers { get; init; }

    public int? Mtu { get; init; }

    public string? MacAddress { get; init; }

    public IReadOnlyList<NetworkRoute>? Routes { get; init; }
}

public sealed record NetworkBondConfig
{
    public IReadOnlyList<string>? Interfaces { get; init; }

    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

public sealed record NetworkBridgeConfig
{
    public IReadOnlyList<string>? Interfaces { get; init; }
}

public sealed record NetworkVlanConfig
{
    public string? Link { get; init; }

    public int? Id { get; init; }
}

public sealed record NetworkNameservers
{
    public IReadOnlyList<string>? Addresses { get; init; }

    public IReadOnlyList<string>? Search { get; init; }
}

public sealed record NetworkRoute
{
    // CIDR or the literal string "default".
    public string? To { get; init; }

    public string? Via { get; init; }

    public int? Metric { get; init; }
}

public enum NetworkDhcpRenderer
{
    Networkd,
    NetworkManager,
    Other,
}
