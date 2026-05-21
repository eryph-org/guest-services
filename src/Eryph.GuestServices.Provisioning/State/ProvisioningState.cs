namespace Eryph.GuestServices.Provisioning.State;

public sealed record ProvisioningState
{
    public string InstanceId { get; init; } = "";

    // HashSet<string> here is ordinal — STJ rebuilds it with the default comparer
    // on deserialize, so do not switch to a case-insensitive set without also adding
    // a custom JsonConverter to round-trip the comparer.
    public HashSet<string> CompletedStages { get; init; } = new(StringComparer.Ordinal);

    public HashSet<string> CompletedHandlers { get; init; } = new(StringComparer.Ordinal);

    public int RebootCount { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastUpdated { get; init; }
}
