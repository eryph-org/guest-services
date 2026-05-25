namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Cloud-init compatible <c>keyboard</c> directive. <see cref="Layout"/> is
/// cross-platform — Linux consumes the X11 layout name; Windows accepts a
/// BCP-47 tag or a <c>language:klid</c> KLID. The X11-only fields
/// (<see cref="Model"/>, <see cref="Variant"/>, <see cref="Options"/>) round-
/// trip on Windows so cross-cloud cloud-config is preserved, but only Linux
/// applies them.
/// </summary>
[CloudInitRecord]
public sealed record KeyboardConfig
{
    /// <summary>
    /// Keyboard layout. Accepts a BCP-47 language tag (e.g. <c>en-US</c>,
    /// <c>de-DE</c>) or a Windows KLID in the <c>language:klid</c> form
    /// (e.g. <c>0409:00000409</c>). When only a language tag is supplied
    /// the default keyboard layout for that language is used.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Keyboard layout — BCP-47 tag, X11 layout, or 'language:klid' on Windows")]
    public string? Layout { get; init; }

    /// <summary>
    /// X11 keyboard model. Linux-only — Windows tzutil-style locale mapping
    /// does not use X11 model strings.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "X11 keyboard model (Linux-only)")]
    public string? Model { get; init; }

    /// <summary>
    /// X11 keyboard variant. Linux-only.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "X11 keyboard variant (Linux-only)")]
    public string? Variant { get; init; }

    /// <summary>
    /// X11 keyboard options. Linux-only.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "X11 keyboard options (Linux-only)")]
    public string? Options { get; init; }
}
