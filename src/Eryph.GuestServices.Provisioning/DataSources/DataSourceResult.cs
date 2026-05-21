using System.Text;
using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed record DataSourceResult
{
    public required string SourceName { get; init; }

    public required string InstanceId { get; init; }

    public string? Hostname { get; init; }

    public string? UserData { get; init; }

    // Raw vendor-data string. The user-data pipeline processes this in a later phase;
    // for v1 we just carry it through.
    public string? VendorData { get; init; }

    // Free-form per-platform key/value bag (e.g. flattened OpenStack meta_data.json).
    // PlatformMetadata captures the well-known subset in a strongly-typed form.
    public IReadOnlyDictionary<string, string> MetaData { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public PlatformMetadata? PlatformMetadata { get; init; }

    // Raw network-config text (YAML or JSON) as the datasource produced it. Kept
    // alongside the structured form so we can log / diagnose without re-serialising.
    public string? NetworkConfig { get; init; }

    public NetworkConfig? StructuredNetworkConfig { get; init; }

    public byte[] GetUserDataBytes() =>
        string.IsNullOrEmpty(UserData) ? [] : Encoding.UTF8.GetBytes(UserData);

    public byte[] GetVendorDataBytes() =>
        string.IsNullOrEmpty(VendorData) ? [] : Encoding.UTF8.GetBytes(VendorData);
}
