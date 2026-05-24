namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_mounts</c> swap-file block. Linux-only; on Windows the
/// page file is OS-managed and there is no direct analogue.
/// </summary>
[CloudInitRecord]
public sealed record SwapConfig
{
    /// <summary>Path of the swap file (default <c>/swap.img</c>).</summary>
    public string? Filename { get; init; }

    /// <summary>Initial size — bytes, suffixes (<c>1G</c>), or <c>auto</c>.</summary>
    public string? Size { get; init; }

    /// <summary>Upper bound for auto sizing.</summary>
    public string? Maxsize { get; init; }
}
