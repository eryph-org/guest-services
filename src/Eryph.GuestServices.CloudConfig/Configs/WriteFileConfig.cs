namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record WriteFileConfig
{
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Target file path")]
    public string? Path { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "File contents")]
    public string? Content { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Owner — user[:group] (cloud-init colon-form)")]
    public string? Owner { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "POSIX permissions in octal form, e.g. '0644'")]
    public string? Permissions { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Content encoding — b64, gzip, gz+b64, text/plain")]
    public string? Encoding { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Append to existing content instead of replacing")]
    public bool? Append { get; init; }

    /// <summary>
    /// Postpone the write until the Final stage (after users are created).
    /// Cloud-init applies deferred entries last so they can reference users
    /// that earlier modules in the same run created. Phase 3 wires the
    /// runtime semantics.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Postpone the write until the Final stage (after users are created)")]
    public bool? Defer { get; init; }
}
