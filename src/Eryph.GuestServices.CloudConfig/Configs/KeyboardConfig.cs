namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Cloud-init compatible <c>keyboard</c> directive — the cross-platform
/// subset that maps to Windows input methods. <c>model</c>, <c>variant</c>
/// and <c>options</c> from cloud-init's full schema are Linux X11
/// concepts with no Windows analogue.
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
    public string? Layout { get; init; }
}
