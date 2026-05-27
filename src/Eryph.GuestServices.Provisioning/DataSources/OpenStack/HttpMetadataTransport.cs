namespace Eryph.GuestServices.Provisioning.DataSources.OpenStack;

/// <summary>
/// <see cref="IOpenStackMetadataTransport"/> over the HTTP metadata service.
/// cloud-init parity: <c>helpers/openstack.py</c> <c>MetadataReader</c>.
/// </summary>
internal sealed class HttpMetadataTransport(OpenStackMetadataClient client) : IOpenStackMetadataTransport
{
    public Task<byte[]?> TryReadAsync(string relativePath, CancellationToken cancellationToken) =>
        client.GetAsync(relativePath, cancellationToken);

    public string Describe(string relativePath) => client.UrlFor(relativePath);
}
