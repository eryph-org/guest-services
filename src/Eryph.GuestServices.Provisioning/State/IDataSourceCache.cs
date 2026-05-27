using Eryph.GuestServices.Provisioning.DataSources;

namespace Eryph.GuestServices.Provisioning.State;

/// <summary>
/// Local cache of the located <see cref="DataSourceResult"/>, mirroring
/// cloud-init's instance cache (<c>/var/lib/cloud/instance/obj.pkl</c> +
/// <c>restore_from_cache</c>). The first boot crawls the datasource and saves the
/// result here; subsequent boots of the same in-progress instance restore from
/// the cache instead of re-probing. This matters for network datasources
/// (OpenStack metadata service): after a module-requested reboot the metadata
/// endpoint may be momentarily unreachable, but the data was already fetched —
/// resume must not depend on reaching it again.
/// </summary>
public interface IDataSourceCache
{
    Task<DataSourceResult?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(DataSourceResult data, CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken);
}
