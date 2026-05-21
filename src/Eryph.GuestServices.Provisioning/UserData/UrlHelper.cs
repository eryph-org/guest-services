using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData;

// Defaults for v1; P2 will lift these into egs-provisioning.json.
//   PER-ATTEMPT TIMEOUT = 30s
//   MAX RETRIES         = 3 (so up to 4 attempts total)
//   BACKOFF             = 1s, 2s, 4s (capped, total < 10s)
//
// Supported schemes: http, https, file. The HttpClient is configured for
// transparent gzip / deflate decompression on the wire; if a server
// delivers a gzipped payload WITHOUT the Content-Encoding header (e.g.
// because the file is genuinely a .gz blob) the caller is expected to
// detect the gzip magic via UserDataContentTypeSniffer.DecompressIfGzipped.
internal sealed class UrlHelper(ILogger<UrlHelper> logger) : IUrlHelper
{
    public const int DefaultMaxAttempts = 4;
    public static readonly TimeSpan DefaultPerAttemptTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(1);

    private static readonly Lazy<HttpClient> SharedHttpClient = new(CreateHttpClient);

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };
        return new HttpClient(handler)
        {
            Timeout = DefaultPerAttemptTimeout,
        };
    }

    public async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"URL '{url}' is not a well-formed absolute URI.");

        if (uri.IsFile)
        {
            // file:// is used for local-disk fixtures and tests; no retry needed.
            var path = uri.LocalPath;
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"Unsupported URL scheme '{uri.Scheme}' for '{url}'.");

        Exception? lastError = null;
        var backoff = DefaultInitialBackoff;

        for (var attempt = 1; attempt <= DefaultMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await SharedHttpClient.Value
                    .GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(
                    ex,
                    "Fetch attempt {Attempt}/{Max} for {Url} failed: {Message}",
                    attempt,
                    DefaultMaxAttempts,
                    url,
                    ex.Message);

                if (attempt >= DefaultMaxAttempts)
                    break;

                try
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 4000));
            }
        }

        throw new HttpRequestException(
            $"Failed to fetch '{url}' after {DefaultMaxAttempts} attempts.",
            lastError);
    }
}
