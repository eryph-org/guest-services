using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Durable per-entry execution record used by <see cref="RuncmdModule"/> to
/// resume cleanly after a reboot-and-continue (cbi exit codes 1001 / 1003).
/// Each entry is identified by (ordinal, content-hash) so an operator edit
/// that keeps the ordinal but changes the command invalidates the marker
/// and the new command runs from scratch.
/// </summary>
public sealed record RuncmdCheckpoint
{
    /// <summary>
    /// Entries that have reached a terminal state in some prior run (exit 0,
    /// non-zero non-1003, or 1001 — the "I'm done, reboot, move on" case).
    /// Skipped on resume.
    /// </summary>
    public IReadOnlyList<RuncmdCheckpointEntry> Completed { get; init; } = [];

    /// <summary>
    /// Per-entry progress for in-flight entries (those that have returned
    /// 1003 at least once but not yet reached a terminal state). Keyed by
    /// <c>"{ordinal}:{contentHash}"</c>. Cleared when the entry finishes.
    /// </summary>
    public Dictionary<string, RuncmdEntryProgress> Progress { get; init; } = new(StringComparer.Ordinal);

    public static RuncmdCheckpoint Empty => new();

    public bool IsCompleted(int ordinal, string contentHash) =>
        Completed.Any(e => e.Ordinal == ordinal &&
                           string.Equals(e.ContentHash, contentHash, StringComparison.Ordinal));

    public static string ProgressKey(int ordinal, string contentHash) =>
        $"{ordinal}:{contentHash}";

    /// <summary>
    /// Stable hash of an entry's command content. Hashes the shell command
    /// or the argv form joined with a separator that cannot collide with
    /// either (NUL). Identity key only — never compared cryptographically.
    /// </summary>
    public static string ComputeContentHash(string? shellCommand, IReadOnlyList<string>? argv)
    {
        var sb = new StringBuilder();
        if (shellCommand is not null)
        {
            sb.Append("shell\0");
            sb.Append(shellCommand);
        }
        else if (argv is not null)
        {
            sb.Append("argv\0");
            sb.Append(argv.Count);
            foreach (var part in argv)
            {
                sb.Append('\0');
                sb.Append(part);
            }
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}

public sealed record RuncmdCheckpointEntry(int Ordinal, string ContentHash);

/// <summary>
/// Progress state for a runcmd entry that has requested at least one 1003
/// reboot.
/// </summary>
public sealed record RuncmdEntryProgress
{
    /// <summary>
    /// Number of times this entry has triggered a reboot (1001 or 1003).
    /// Compared against the effective per-entry limit.
    /// </summary>
    public int RebootAttempts { get; init; }

    /// <summary>
    /// Script-supplied override of the per-entry limit, picked up from the
    /// <c>EGS_RUNCMD_REBOOT_LIMIT=&lt;n&gt;</c> stdout marker. Null when the
    /// script never emitted a marker; the configured default then applies.
    /// </summary>
    public int? OverrideLimit { get; init; }
}

[JsonSerializable(typeof(RuncmdCheckpoint))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class RuncmdCheckpointJsonContext : JsonSerializerContext;
