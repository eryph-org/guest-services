namespace Eryph.GuestServices.Provisioning.Stages;

public abstract record HandlerOutcome
{
    public sealed record Completed : HandlerOutcome
    {
        public static readonly Completed Instance = new();
    }

    public sealed record RebootRequested(string Reason) : HandlerOutcome;

    public sealed record Failed(string Reason, Exception? Exception = null) : HandlerOutcome;

    public static HandlerOutcome Ok() => Completed.Instance;

    public static HandlerOutcome Reboot(string reason) => new RebootRequested(reason);

    public static HandlerOutcome Fail(string reason, Exception? exception = null) =>
        new Failed(reason, exception);
}
