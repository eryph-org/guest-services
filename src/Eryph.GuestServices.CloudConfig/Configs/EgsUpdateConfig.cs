namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// eryph guest-services self-update directive (<c>egs.update:</c>). Opt-in:
/// the agent only updates itself when <see cref="Enabled"/> is <c>true</c>.
/// The target is either a pinned <see cref="Version"/> or, when none is given,
/// the newest release on the selected <see cref="Channel"/>. A target equal to
/// the running version is a no-op.
/// </summary>
[CloudInitRecord]
public sealed record EgsUpdateConfig
{
    /// <summary>
    /// Master opt-in. When not <c>true</c> the agent performs no self-update,
    /// regardless of the other fields.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Pin to an exact release version (e.g. <c>"0.4.0"</c>). When set it wins
    /// over <see cref="Channel"/>. When omitted the newest version on the
    /// channel is used.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Release channel when no <see cref="Version"/> is pinned: <c>stable</c>
    /// (the index's <c>latestStableVersion</c>) or <c>unstable</c> (its
    /// <c>latestVersion</c>, which may be a preview). Defaults to
    /// <c>stable</c>.
    /// </summary>
    public string? Channel { get; init; }
}
