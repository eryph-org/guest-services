namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Outcome of attempting to extend one volume's hosting partition.
/// Reported back to the module for logging; the module itself only cares
/// about throw vs. no-throw.
/// </summary>
public sealed record VolumeExtendResult
{
    /// <summary>
    /// Uppercase drive letter when the volume is mounted on one, otherwise
    /// null (raw / hidden / unmounted volume).
    /// </summary>
    public char? DriveLetter { get; init; }

    /// <summary>Stable WMI volume identifier — useful when there is no drive letter.</summary>
    public string VolumeId { get; init; } = string.Empty;

    /// <summary>Partition size before the attempt (bytes).</summary>
    public ulong SizeBefore { get; init; }

    /// <summary>Partition size after the attempt (bytes). Equals SizeBefore when no growth was possible.</summary>
    public ulong SizeAfter { get; init; }

    /// <summary>True when SizeAfter &gt; SizeBefore.</summary>
    public bool Extended => SizeAfter > SizeBefore;
}
