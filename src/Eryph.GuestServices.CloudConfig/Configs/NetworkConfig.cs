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
    // The cloud-init v2 'match' clause is a sub-object (name / macaddress / driver)
    // used to bind this entry to a physical NIC. On Windows we match by MAC, so
    // Match.MacAddress is the selector the applier honours. v1 does not use it
    // (v1 carries the MAC on the entry as mac_address -> MacAddress).
    public NetworkMatch? Match { get; init; }

    public bool? Dhcp4 { get; init; }

    public bool? Dhcp6 { get; init; }

    public IReadOnlyList<string>? Addresses { get; init; }

    public string? Gateway4 { get; init; }

    public string? Gateway6 { get; init; }

    public NetworkNameservers? Nameservers { get; init; }

    public int? Mtu { get; init; }

    public string? MacAddress { get; init; }

    public IReadOnlyList<NetworkRoute>? Routes { get; init; }

    // v2 keys that are present in the document but NOT applied on Windows
    // (e.g. dhcp4-overrides, routing-policy, set-name). Captured at parse time
    // purely so the applier can warn instead of silently dropping them — the
    // names are the cloud-init/netplan spellings, for the log message.
    public IReadOnlyList<string>? UnsupportedOptions { get; init; }
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

// cloud-init v2 'match' selector. Any combination of name (glob), macaddress
// and driver may be present; cloud-init applies the entry to NICs matching all
// the given criteria. The Windows applier only resolves adapters by MAC today.
public sealed record NetworkMatch
{
    public string? Name { get; init; }

    public string? MacAddress { get; init; }

    public string? Driver { get; init; }
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
