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
    /// Adapters without a usable link-layer address are not enumerated, so this
    /// is always populated.
    /// </summary>
    public required string MacAddress { get; init; }
}
