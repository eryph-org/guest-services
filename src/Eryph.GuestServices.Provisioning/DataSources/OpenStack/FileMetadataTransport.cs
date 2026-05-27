namespace Eryph.GuestServices.Provisioning.DataSources.OpenStack;

/// <summary>
/// <see cref="IOpenStackMetadataTransport"/> over a mounted ConfigDrive (config-2)
/// volume rooted at <paramref name="root"/>. cloud-init parity:
/// <c>helpers/openstack.py</c> <c>ConfigDriveReader</c>.
/// </summary>
internal sealed class FileMetadataTransport(string root) : IOpenStackMetadataTransport
{
    public async Task<byte[]?> TryReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = ToLocalPath(relativePath);
        if (!File.Exists(path))
            return null;
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public string Describe(string relativePath) => ToLocalPath(relativePath);

    private string ToLocalPath(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([root, .. segments]);
    }
}
