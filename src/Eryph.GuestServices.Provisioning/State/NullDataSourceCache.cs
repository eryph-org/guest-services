using Eryph.GuestServices.Provisioning.DataSources;

namespace Eryph.GuestServices.Provisioning.State;

/// <summary>
/// No-op <see cref="IDataSourceCache"/> used by <c>--dry-run</c>: a what-if run
/// must never persist a datasource cache that a later real run would restore.
/// </summary>
internal sealed class NullDataSourceCache : IDataSourceCache
{
    public Task<DataSourceResult?> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<DataSourceResult?>(null);

    public Task SaveAsync(DataSourceResult data, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ResetAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
