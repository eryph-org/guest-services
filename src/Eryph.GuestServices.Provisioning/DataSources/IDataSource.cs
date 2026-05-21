namespace Eryph.GuestServices.Provisioning.DataSources;

public interface IDataSource
{
    string Name { get; }

    Task<DataSourceResult?> TryDiscoverAsync(CancellationToken cancellationToken);
}
