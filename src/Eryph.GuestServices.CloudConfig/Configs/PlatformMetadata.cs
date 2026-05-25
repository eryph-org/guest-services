namespace Eryph.GuestServices.CloudConfig;

// Structured platform metadata mirroring cloud-init's well-known fields.
// Captured as a separate POCO so the raw MetaData bag on DataSourceResult can stay
// platform-specific while handlers consume a normalised view.
public sealed record PlatformMetadata
{
    public string? LocalHostname { get; init; }

    // Cloud-provider-injected SSH keys (e.g. Azure ovf-env keys, EC2 IMDS public-keys).
    public IReadOnlyList<string>? PublicKeys { get; init; }

    public string? AvailabilityZone { get; init; }

    public string? Region { get; init; }

    // Stable platform identifier — "azure" | "ec2" | "openstack" | "nocloud" | "hyperv" | ...
    public string? CloudName { get; init; }

    public string? Platform { get; init; }

    // cloud-init concept: "metadata" / "config-drive" / etc.
    public string? Subplatform { get; init; }

    public string? InstanceType { get; init; }
}
