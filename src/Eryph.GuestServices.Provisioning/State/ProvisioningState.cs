namespace Eryph.GuestServices.Provisioning.State;

public sealed record ProvisioningState
{
    public string InstanceId { get; init; } = "";

    public HashSet<string> CompletedStages { get; init; } = new(StringComparer.Ordinal);

    public HashSet<string> CompletedHandlers { get; init; } = new(StringComparer.Ordinal);

    public int RebootCount { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastUpdated { get; init; }
}
