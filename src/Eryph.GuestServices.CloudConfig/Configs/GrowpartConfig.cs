namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Cloud-init compatible <c>growpart</c> directive. Mirrors cloud-init's
/// schema (<c>mode</c>, <c>devices</c>) but is interpreted in Windows terms
/// by the provisioning agent — see <c>GrowpartModule</c>.
/// </summary>
public sealed record GrowpartConfig
{
    /// <summary>
    /// <c>auto</c> (default) extends every targeted volume that has free
    /// space behind it. <c>off</c> (or <c>false</c>) disables the module.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Devices to target. Defaults to <c>['/']</c> (cloud-init parity) which
    /// resolves to the Windows system drive. Each entry is one of:
    /// <list type="bullet">
    ///   <item><c>/</c> — the system drive (<c>%SystemDrive%</c>).</item>
    ///   <item>A drive letter — <c>C</c>, <c>"C:"</c>, or <c>"D:\"</c>.
    ///   When the colon is present the value MUST be quoted in YAML, otherwise
    ///   <c>- C:</c> is parsed as an empty mapping, not the string <c>"C:"</c>.</item>
    ///   <item><c>all</c> — every volume that can grow.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<string>? Devices { get; init; }
}
