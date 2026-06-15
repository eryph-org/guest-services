namespace Eryph.GuestServices.Provisioning.Modules;

public abstract record ModuleOutcome
{
    public sealed record Completed : ModuleOutcome
    {
        public static readonly Completed Instance = new();
    }

    /// <summary>
    /// Module is asking the runner to reboot the guest. When
    /// <see cref="IsScriptDriven"/> is true the reboot was triggered by user
    /// code (a runcmd entry or user script returning 1001/1003), not by the
    /// module itself — the StageRunner does not count it toward the
    /// per-module reboot cap. The module is expected to enforce its own
    /// per-entry / per-script quota in that case.
    /// </summary>
    public sealed record RebootRequested(string Reason, bool IsScriptDriven = false) : ModuleOutcome;

    /// <summary>
    /// Module has staged a verified self-update and is asking the host to apply
    /// it. The runner stops the host (no OS reboot); an out-of-process updater
    /// launched from <see cref="StagingDirectory"/> stops the service, swaps the
    /// binaries, and restarts — after which provisioning resumes on the new
    /// binary. Treated like a reboot for resume bookkeeping (the module
    /// re-enters once the new version is running).
    /// </summary>
    public sealed record UpdateRequested(string Reason, string StagingDirectory, string TargetVersion) : ModuleOutcome;

    public sealed record Failed(string Reason, Exception? Exception = null) : ModuleOutcome;

    public static ModuleOutcome Ok() => Completed.Instance;

    public static ModuleOutcome Reboot(string reason) => new RebootRequested(reason);

    public static ModuleOutcome Update(string reason, string stagingDirectory, string targetVersion) =>
        new UpdateRequested(reason, stagingDirectory, targetVersion);

    /// <summary>
    /// Reboot requested on behalf of user-supplied code (runcmd entry or
    /// user script). The StageRunner skips the per-module reboot cap; the
    /// calling module is responsible for its own quota.
    /// </summary>
    public static ModuleOutcome RebootForUserScript(string reason) =>
        new RebootRequested(reason, IsScriptDriven: true);

    public static ModuleOutcome Fail(string reason, Exception? exception = null) =>
        new Failed(reason, exception);
}
