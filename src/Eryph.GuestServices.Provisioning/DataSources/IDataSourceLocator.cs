namespace Eryph.GuestServices.Provisioning.DataSources;

public interface IDataSourceLocator
{
    Task<DataSourceResult?> LocateAsync(CancellationToken cancellationToken);
}
