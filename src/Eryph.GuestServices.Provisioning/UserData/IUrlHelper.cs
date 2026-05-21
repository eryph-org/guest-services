namespace Eryph.GuestServices.Provisioning.UserData;

// Fetches bytes from an http/https/file URL. Implementations transparently
// decompress gzip-encoded HTTP responses and retry on transient failures.
public interface IUrlHelper
{
    Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken);
}
