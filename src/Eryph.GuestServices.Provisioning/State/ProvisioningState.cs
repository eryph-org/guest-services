namespace Eryph.GuestServices.Provisioning.State;

public sealed record ProvisioningState
{
    public string InstanceId { get; init; } = "";

    // HashSet<string> here is ordinal — STJ rebuilds it with the default comparer
    // on deserialize, so do not switch to a case-insensitive set without also adding
    // a custom JsonConverter to round-trip the comparer.
    public HashSet<string> CompletedStages { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Deprecated: kept for one release to ease the migration from the
    /// single-blob state file to per-module semaphores
    /// (<c>%ProgramData%\eryph\provisioning\instance\&lt;id&gt;\sem\&lt;module&gt;.per-instance</c>).
    /// On startup the stage runner reads any handler keys here, writes the
    /// equivalent per-instance semaphore files, then keeps the list in sync
    /// as a redundant view. New code should consult <c>ISemaphoreStore</c>
    /// directly. Remove this field once all rolled-out agents have migrated.
    /// </summary>
    public HashSet<string> CompletedHandlers { get; init; } = new(StringComparer.Ordinal);

    public int RebootCount { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastUpdated { get; init; }
}
