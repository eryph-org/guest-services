namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record GroupConfig
{
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Group name")]
    public string? Name { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Group members")]
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Numeric group ID. Cloud-init's groups schema accepts either
    /// <c>name: [members]</c> or <c>name: {members: [...], gid: int}</c> —
    /// the latter form lets the operator pin the gid. Linux-only — Windows
    /// local groups do not have a portable numeric identifier.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Numeric group ID (no Windows analogue)")]
    public int? Gid { get; init; }
}
