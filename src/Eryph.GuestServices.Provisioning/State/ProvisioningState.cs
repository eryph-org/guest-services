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
    ///
    /// Only handlers that have reached <c>Completed</c> appear here. A handler
    /// that returned <c>RebootRequested</c> shows up in <see cref="PendingHandlers"/>
    /// until it completes its post-reboot resume. Pre-fix, both states were
    /// indistinguishable (docs/bugs/0001).
    /// </summary>
    public HashSet<string> CompletedHandlers { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Handlers that have returned <see cref="Modules.ModuleOutcome.RebootRequested"/>
    /// at least once and are still waiting to resume. Promoted to
    /// <see cref="CompletedHandlers"/> on the run after the reboot if the
    /// re-entered module returns <see cref="Modules.ModuleOutcome.Completed"/>.
    /// Distinct from <c>CompletedHandlers</c> so external consumers (eryph-genes
    /// Pester) can tell "all done" from "stuck pending reboot resume".
    /// </summary>
    public HashSet<string> PendingHandlers { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-module count of how many times the StageRunner has seen a
    /// <see cref="Modules.ModuleOutcome.RebootRequested"/> outcome for that
    /// module key on this instance. Capped via <see cref="StageRunner"/>'s
    /// per-module limit so a script that keeps returning 1003 fails the run
    /// instead of looping forever (docs/bugs/0001 "loop-safety").
    /// </summary>
    public Dictionary<string, int> ModuleRebootCounts { get; init; } = new(StringComparer.Ordinal);

    public int RebootCount { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastUpdated { get; init; }
}
