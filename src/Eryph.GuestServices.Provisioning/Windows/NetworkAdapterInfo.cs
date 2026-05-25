namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Snapshot of a Windows network adapter as needed by the network applier.
/// Returned by <see cref="IWindowsOs.GetNetworkAdaptersAsync"/>. The MAC is
/// normalised to a colon-separated lowercase form so callers can compare
/// directly with cloud-init's <c>macaddress</c> field.
/// </summary>
public sealed record NetworkAdapterInfo
{
    /// <summary>
    /// Cross-cmdlet identifier on Windows (e.g. "Ethernet", "Ethernet 2").
    /// Used to scope follow-up Set-* / New-* calls to the right adapter.
    /// </summary>
    public required string InterfaceAlias { get; init; }

    /// <summary>Stable index that survives renames; never zero.</summary>
    public required int InterfaceIndex { get; init; }

    /// <summary>
    /// Canonical MAC: 12 lowercase hex digits separated by single colons.
    /// Empty if the adapter has no link-layer address (loopback, tunnel).
    /// </summary>
    public required string MacAddress { get; init; }

    /// <summary>
    /// True iff the adapter is "physical" in the MSFT_NetAdapter sense —
    /// hardware NIC backed by a driver, not a loopback / tunnel / virtual
    /// switch endpoint. The applier ignores non-physical adapters per RFC 0002.
    /// </summary>
    public required bool IsPhysical { get; init; }
}
