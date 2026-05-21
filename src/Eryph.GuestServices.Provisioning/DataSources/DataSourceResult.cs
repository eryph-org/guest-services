using System.Text;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed record DataSourceResult
{
    public required string SourceName { get; init; }

    public required string InstanceId { get; init; }

    public string? Hostname { get; init; }

    public string? UserData { get; init; }

    public IReadOnlyDictionary<string, string> MetaData { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? NetworkConfig { get; init; }

    public byte[] GetUserDataBytes() =>
        string.IsNullOrEmpty(UserData) ? [] : Encoding.UTF8.GetBytes(UserData);
}
