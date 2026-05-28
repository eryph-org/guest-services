using System.Text.Json.Serialization;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Per-instance execution record used by both <see cref="RuncmdModule"/> and
/// <see cref="ScriptsUserModule"/> to resume cleanly after a cbi-style
/// reboot-and-continue (exit 1001 / 1003). An item is identified by
/// (ordinal, hash); editing an item between runs changes the hash and
/// invalidates its checkpoint state.
/// </summary>
public sealed record UserCodeCheckpoint
{
    /// <summary>
    /// Items that have reached a terminal state in some prior run
    /// (exit 0 / 1001 / 1002 / non-1003 error). Skipped on resume.
    /// </summary>
    public IReadOnlyList<CheckpointEntry> Completed { get; init; } = [];

    /// <summary>
    /// In-flight items — those that returned 1003 ("reboot, re-run me") at
    /// least once but have not yet reached a terminal state. Keyed by
    /// <c>"{ordinal}:{hash}"</c>. Cleared when the item finishes.
    /// </summary>
    public Dictionary<string, EntryProgress> Progress { get; init; } = new(StringComparer.Ordinal);

    public static UserCodeCheckpoint Empty => new();

    public bool IsCompleted(int ordinal, string hash) =>
        Completed.Any(e => e.Ordinal == ordinal &&
                           string.Equals(e.Hash, hash, StringComparison.Ordinal));

    public static string ProgressKey(int ordinal, string hash) =>
        $"{ordinal}:{hash}";
}

public sealed record CheckpointEntry(int Ordinal, string Hash);

/// <summary>
/// Mutable state for an in-flight item.
/// </summary>
public sealed record EntryProgress
{
    /// <summary>
    /// Number of times this item has triggered a reboot (1001 or 1003).
    /// Compared against the effective per-script limit.
    /// </summary>
    public int RebootAttempts { get; init; }

    /// <summary>
    /// Script-supplied override of the per-script limit, picked up from the
    /// <c>##egs.reboot_limit=&lt;n&gt;</c> stdout directive. Null when the
    /// script never emitted a directive; the configured default then applies.
    /// </summary>
    public int? OverrideLimit { get; init; }
}

[JsonSerializable(typeof(UserCodeCheckpoint))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class UserCodeCheckpointJsonContext : JsonSerializerContext;
