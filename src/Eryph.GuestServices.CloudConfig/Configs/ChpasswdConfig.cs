namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record ChpasswdConfig
{
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Expire passwords on first login")]
    public bool? Expire { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Per-user password entries (cloud-init users-form chpasswd)")]
    [MergeBehavior(MergeKind.KeyedByName, KeyedMergeMethod = "MergeChpasswdEntry")]
    public IReadOnlyList<ChpasswdListEntry>? Users { get; init; }

    /// <summary>
    /// Legacy newline-separated <c>user:password</c> list form. Cloud-init
    /// still supports it as of 24.x even though the per-user <c>users</c>
    /// form is preferred — keep accepting it so cross-cloud cloud-config
    /// authored against older docs round-trips cleanly.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Legacy newline-separated user:password list; cloud-init still supports it as of 24.x")]
    public string? List { get; init; }
}
