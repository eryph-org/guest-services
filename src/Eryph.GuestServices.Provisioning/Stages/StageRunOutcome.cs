namespace Eryph.GuestServices.Provisioning.Stages;

public abstract record StageRunOutcome
{
    public sealed record Success : StageRunOutcome
    {
        public static readonly Success Instance = new();
    }

    public sealed record RebootRequested(string Reason) : StageRunOutcome;

    /// <summary>
    /// A module staged a self-update. The host launches the updater process
    /// from <see cref="StagingDirectory"/> and stops (no OS reboot); the updater
    /// restarts the service onto the new binary, where provisioning resumes.
    /// </summary>
    public sealed record UpdateRequested(string Reason, string StagingDirectory, string TargetVersion) : StageRunOutcome;

    public sealed record Failed(string Reason, Exception? Exception = null) : StageRunOutcome;

    public sealed record NoDataSource : StageRunOutcome
    {
        public static readonly NoDataSource Instance = new();
    }
}
