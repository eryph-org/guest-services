namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// One entry of the cloud-init-compatible <c>packages:</c> list. cloud-init
/// allows each entry to be a bare package name, a <c>[name, version]</c> pair,
/// or a manager-keyed mapping (e.g. <c>{apt: [...], snap: [...]}</c>). eryph
/// extends the manager set with <c>winget</c> and <c>chocolatey</c> for Windows
/// (RFC 0033). A bare name / pair uses the default provider (winget on Windows);
/// Linux managers (<c>apt</c>, <c>snap</c>, <c>yum</c>, …) are accepted and
/// no-op on Windows.
/// </summary>
/// <remarks>
/// Advanced per-package options (winget <c>source</c>/<c>scope</c>, chocolatey
/// <c>params</c>) are not expressed here — use the dedicated <c>winget:</c> /
/// <c>chocolatey:</c> blocks for those. The umbrella covers the common
/// name(+version) case and routes into the same modules.
/// </remarks>
[CloudInitRecord]
public sealed record PackageEntry
{
    /// <summary>
    /// Bare package name for the default provider — set when the entry is a
    /// scalar or a <c>[name, version]</c> pair. Mutually exclusive with
    /// <see cref="Managers"/>.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>Pinned version, from the second element of a <c>[name, version]</c> pair.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Manager-keyed groups (<c>winget</c>, <c>chocolatey</c>, and the accepted
    /// Linux managers). Set when the entry is a mapping. Mutually exclusive with
    /// <see cref="Name"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ManagerPackage>>? Managers { get; init; }
}

/// <summary>
/// One package inside a manager group of a <see cref="PackageEntry"/>. A bare
/// scalar sets only <see cref="Name"/>; a mapping may also set
/// <see cref="Version"/> (the key may be <c>name</c> or, for winget, <c>id</c>).
/// </summary>
[CloudInitRecord]
public sealed record ManagerPackage
{
    public string Name { get; init; } = "";

    public string? Version { get; init; }
}
