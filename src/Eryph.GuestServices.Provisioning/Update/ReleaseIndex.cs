using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>
/// The release index published at
/// <c>https://releases.dbosoft.eu/eryph/guest-services/index.json</c> — the
/// same document the <c>egs-tool</c> installer reads. Only the fields the
/// self-updater needs are modelled; unknown fields are ignored.
/// </summary>
public sealed record ReleaseIndex
{
    [JsonPropertyName("latestVersion")]
    public string? LatestVersion { get; init; }

    [JsonPropertyName("latestStableVersion")]
    public string? LatestStableVersion { get; init; }

    [JsonPropertyName("versions")]
    public IReadOnlyDictionary<string, ReleaseVersion>? Versions { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Parses the index JSON. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static ReleaseIndex Parse(string json) =>
        JsonSerializer.Deserialize<ReleaseIndex>(json, Options)
        ?? throw new JsonException("Release index deserialized to null.");
}

public sealed record ReleaseVersion
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<ReleaseFile>? Files { get; init; }
}

public sealed record ReleaseFile
{
    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("sha256Checksum")]
    public string? Sha256Checksum { get; init; }

    [JsonPropertyName("os")]
    public string? Os { get; init; }

    [JsonPropertyName("arch")]
    public string? Arch { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }
}
