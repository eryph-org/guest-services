namespace Eryph.GuestServices.Provisioning.DataSources;

// Cloud-init-style probe-state datasource. Each datasource is probed in priority
// order; ProbeAsync returns one of the four outcomes below. A WaitForReady result
// asks the locator to back off and retry the same datasource (e.g. while a native
// provisioner like Azure PA is still running). OnCompletedAsync fires after the
// stage runner finishes provisioning successfully so the datasource can clean up.
public interface IDataSource
{
    string Name { get; }

    // Lower values are probed first.
    int Priority { get; }

    bool RequiresNetwork { get; }

    Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken);

    Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken);
}

public abstract record DataSourceProbeResult
{
    public sealed record NotApplicable : DataSourceProbeResult
    {
        public static readonly NotApplicable Instance = new();
    }

    public sealed record WaitForReady(string Reason, TimeSpan Backoff) : DataSourceProbeResult;

    public sealed record Ready(DataSourceResult Data) : DataSourceProbeResult;

    public sealed record Failed(string Reason, Exception? Exception = null) : DataSourceProbeResult;
}
