namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_ssh</c> <c>ssh_publish_hostkeys:</c> block — publishes
/// per-instance ssh host keys to the platform metadata service. Linux-only.
/// </summary>
[CloudInitRecord]
public sealed record SshPublishHostKeysConfig
{
    /// <summary>Master enable flag.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Key types that are explicitly excluded from publication.</summary>
    public IReadOnlyList<string>? Blacklist { get; init; }
}
