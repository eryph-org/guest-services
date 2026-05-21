namespace Eryph.GuestServices.Provisioning.DataSources;

/// <summary>
/// A datasource that always reports Ready with caller-supplied user-data and
/// instance-id. Used by the CLI <c>run --user-data &lt;path&gt;</c> and
/// <c>run --instance-id &lt;id&gt;</c> options. Registered ahead of the
/// auto-discovery chain so it wins.
/// </summary>
internal sealed class OverrideDataSource(string instanceId, string? userData) : IDataSource
{
    public string Name => "override";

    // Lower than any of the real datasource priorities (Azure starts at 10).
    public int Priority => 0;

    public bool RequiresNetwork => false;

    public Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<DataSourceProbeResult>(new DataSourceProbeResult.Ready(new DataSourceResult
        {
            SourceName = Name,
            InstanceId = instanceId,
            UserData = userData,
        }));

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
