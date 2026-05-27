namespace Eryph.GuestServices.Provisioning.DataSources.OpenStack;

/// <summary>
/// Abstracts "fetch a file under the OpenStack metadata tree" so the same reader
/// logic serves both the ConfigDrive (disk) and metadata-service (HTTP) variants.
/// Mirrors cloud-init's <c>helpers/openstack.py</c> <c>BaseReader</c>, whose
/// <c>ConfigDriveReader</c> and <c>MetadataReader</c> differ only in how a path
/// is read.
///
/// Relative paths are forward-slash, rooted at the drive/endpoint base, e.g.
/// <c>openstack/2018-08-27/meta_data.json</c> or <c>openstack/latest/user_data</c>.
/// </summary>
internal interface IOpenStackMetadataTransport
{
    /// <summary>
    /// Reads the resource at <paramref name="relativePath"/>. Returns the bytes
    /// on success, or <c>null</c> when the resource is absent (a missing file /
    /// HTTP 404). Anything else — malformed transport state, a non-404 HTTP
    /// error after retries — throws.
    /// </summary>
    Task<byte[]?> TryReadAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>
    /// Human-readable location of <paramref name="relativePath"/> (a file path or
    /// URL) for diagnostics and exception messages.
    /// </summary>
    string Describe(string relativePath);
}
