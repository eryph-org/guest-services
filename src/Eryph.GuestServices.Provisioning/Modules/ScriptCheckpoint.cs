using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Durable per-script execution record used by <see cref="ScriptsUserModule"/>
/// to resume after a 1003 reboot-and-continue without re-running already
/// executed scripts. See docs/bugs/0001-scriptsusermodule-skips-queue-after-reboot.md.
/// </summary>
public sealed record ScriptCheckpoint
{
    public IReadOnlyList<ScriptCheckpointEntry> Executed { get; init; } = [];

    /// <summary>
    /// Per-script reboot quota: number of times a given (ordinal, body-hash)
    /// pair has been recorded as having returned 1003 without subsequent
    /// progress. Exceeding the configured quota is treated as a hard failure
    /// (loop-safety per docs/bugs/0001).
    /// </summary>
    public Dictionary<string, int> RebootCounts { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-script raised reboot limit, set by a script emitting
    /// <c>##egs.reboot_limit=&lt;n&gt;</c> on stdout. Persisted so the script
    /// only needs to emit the directive once. Null entry (or missing key) =
    /// no override; the configured default applies.
    /// </summary>
    public Dictionary<string, int> OverrideLimits { get; init; } = new(StringComparer.Ordinal);

    public static ScriptCheckpoint Empty => new();

    public bool Contains(int ordinal, string bodyHash) =>
        Executed.Any(e => e.Ordinal == ordinal &&
                          string.Equals(e.BodyHash, bodyHash, StringComparison.Ordinal));

    /// <summary>
    /// Stable hash of the script body. SHA-256 of the raw bytes — purely an
    /// identity key, never compared cryptographically.
    /// </summary>
    public static string ComputeBodyHash(byte[] body)
    {
        var hash = SHA256.HashData(body);
        return Convert.ToHexString(hash);
    }
}

public sealed record ScriptCheckpointEntry(int Ordinal, string BodyHash);

[JsonSerializable(typeof(ScriptCheckpoint))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ScriptCheckpointJsonContext : JsonSerializerContext;
