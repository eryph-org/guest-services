namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record ChpasswdListEntry
{
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "User name to set the password for")]
    public string? Name { get; init; }

    /// <summary>
    /// Password value. Cloud-init's list-form treats the literal tokens
    /// <c>R</c> or <c>RANDOM</c> as the random-generation token; Phase 3
    /// wires the runtime semantics. <see cref="Type"/> disambiguates plain
    /// text from a pre-hashed value.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Password value (literal 'R' / 'RANDOM' selects random-generation when Type=RANDOM)")]
    public string? Password { get; init; }

    /// <summary>
    /// Password type: <c>RANDOM</c> | <c>hash</c> | <c>text</c>. Cloud-init's
    /// chpasswd schema uses this to disambiguate how <see cref="Password"/>
    /// should be applied.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Password kind — RANDOM, hash, or text")]
    public string? Type { get; init; }
}
