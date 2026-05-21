namespace Eryph.GuestServices.Provisioning.Modules;

public abstract record ModuleOutcome
{
    public sealed record Completed : ModuleOutcome
    {
        public static readonly Completed Instance = new();
    }

    public sealed record RebootRequested(string Reason) : ModuleOutcome;

    public sealed record Failed(string Reason, Exception? Exception = null) : ModuleOutcome;

    public static ModuleOutcome Ok() => Completed.Instance;

    public static ModuleOutcome Reboot(string reason) => new RebootRequested(reason);

    public static ModuleOutcome Fail(string reason, Exception? exception = null) =>
        new Failed(reason, exception);
}
