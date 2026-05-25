namespace Eryph.GuestServices.Provisioning.Stages;

public abstract record StageRunOutcome
{
    public sealed record Success : StageRunOutcome
    {
        public static readonly Success Instance = new();
    }

    public sealed record RebootRequested(string Reason) : StageRunOutcome;

    public sealed record Failed(string Reason, Exception? Exception = null) : StageRunOutcome;

    public sealed record NoDataSource : StageRunOutcome
    {
        public static readonly NoDataSource Instance = new();
    }
}
