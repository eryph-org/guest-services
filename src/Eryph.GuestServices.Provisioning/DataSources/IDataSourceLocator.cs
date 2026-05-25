namespace Eryph.GuestServices.Provisioning.DataSources;

public interface IDataSourceLocator
{
    Task<DataSourceResult?> LocateAsync(CancellationToken cancellationToken);

    // Called by the stage runner once provisioning has fully succeeded, so the
    // originating datasource can clean up (e.g. delete Azure CustomData.bin).
    Task OnProvisioningCompletedAsync(DataSourceResult data, CancellationToken cancellationToken);
}
