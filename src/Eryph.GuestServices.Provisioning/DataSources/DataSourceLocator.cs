using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class DataSourceLocator(
    IEnumerable<IDataSource> dataSources,
    ILogger<DataSourceLocator> logger) : IDataSourceLocator
{
    public async Task<DataSourceResult?> LocateAsync(CancellationToken cancellationToken)
    {
        foreach (var source in dataSources)
        {
            logger.LogDebug("Probing data source {Name}", source.Name);
            var result = await source.TryDiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                logger.LogInformation(
                    "Data source {Name} produced result for instance {InstanceId}",
                    source.Name,
                    result.InstanceId);
                return result;
            }
        }

        logger.LogWarning("No data source produced provisioning data");
        return null;
    }
}
