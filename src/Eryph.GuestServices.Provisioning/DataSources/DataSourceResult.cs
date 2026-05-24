using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed record DataSourceResult
{
    public required string SourceName { get; init; }

    public required string InstanceId { get; init; }

    public string? Hostname { get; init; }

    // Datasource-supplied default admin account name (cloud-init
    // system_info.default_user.name equivalent surfaced by the platform's
    // metadata). Feeds layer 2 of IDefaultUserResolver. Currently always null:
    // no datasource populates it yet.
    // TODO(RFC 0018 / Findings 19-20): OpenStack admin_pass / known-admin name surfaces here.
    public string? DefaultUserName { get; init; }

    // Raw user-data bytes. MUST stay as binary end-to-end: real-world user-data
    // is frequently gzip-compressed (eryph-zero's configdrive ships gzipped
    // multipart MIME), and the gzip header (1F 8B 08 ...) is not valid UTF-8.
    // ReadAllText would replace 0x8B with the U+FFFD replacement character
    // (3 bytes EF BF BD) and silently destroy the payload. The pipeline expects
    // exactly the bytes the datasource produced.
    public byte[]? UserData { get; init; }

    // Same rule applies to vendor-data: it may be gzipped multipart MIME too.
    public byte[]? VendorData { get; init; }

    // Free-form per-platform key/value bag (e.g. flattened OpenStack meta_data.json).
    // PlatformMetadata captures the well-known subset in a strongly-typed form.
    public IReadOnlyDictionary<string, string> MetaData { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public PlatformMetadata? PlatformMetadata { get; init; }

    // Raw network-config text (YAML or JSON) as the datasource produced it. Kept
    // alongside the structured form so we can log / diagnose without re-serialising.
    public string? NetworkConfig { get; init; }

    public NetworkConfig? StructuredNetworkConfig { get; init; }

    public byte[] GetUserDataBytes() => UserData ?? [];

    public byte[] GetVendorDataBytes() => VendorData ?? [];
}
