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
    /// <see cref="IWindowsOs.GetNetworkAdaptersAsync"/> only yields adapters
    /// with a usable hardware address, so values it returns are populated; a
    /// hand-constructed instance may carry anything.
    /// </summary>
    public required string MacAddress { get; init; }
}
