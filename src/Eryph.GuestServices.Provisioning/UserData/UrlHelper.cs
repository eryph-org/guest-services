using System.Net;
using System.Net.Http;
using Eryph.GuestServices.Provisioning.Configuration;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData;

// Supported schemes: http, https, file. The HttpClient is configured for
// transparent gzip / deflate decompression on the wire; if a server
// delivers a gzipped payload WITHOUT the Content-Encoding header (e.g.
// because the file is genuinely a .gz blob) the caller is expected to
// detect the gzip magic via UserDataContentTypeSniffer.DecompressIfGzipped.
internal sealed class UrlHelper : IUrlHelper
{
    private readonly ILogger<UrlHelper> _logger;
    private readonly HttpClient _httpClient;
    private readonly int _maxAttempts;
    private readonly TimeSpan _initialBackoff;
    private readonly long _maxBytes;

    public UrlHelper(ILogger<UrlHelper> logger, ProvisioningSettings? settings = null)
    {
        _logger = logger;
        var s = settings ?? new ProvisioningSettings();
        _maxAttempts = Math.Max(1, s.UserData.FetchMaxAttempts);
        _initialBackoff = TimeSpan.FromSeconds(Math.Max(0, s.UserData.FetchInitialBackoffSeconds));
        _maxBytes = Math.Max(1, s.UserData.FetchMaxBytes);
        var perAttemptTimeout = TimeSpan.FromSeconds(Math.Max(1, s.UserData.FetchTimeoutSeconds));

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = perAttemptTimeout,
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
            await using var fileStream = File.OpenRead(path);
            return await ReadBoundedAsync(fileStream, url, cancellationToken).ConfigureAwait(false);
        }

        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"Unsupported URL scheme '{uri.Scheme}' for '{url}'.");

        Exception? lastError = null;
        var backoff = _initialBackoff;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await _httpClient
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // Reject early when the server advertises an oversized body.
                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is > 0 && declaredLength > _maxBytes)
                    throw new HttpRequestException(
                        $"Response for '{url}' reports {declaredLength} bytes, exceeding the {_maxBytes}-byte cap.");

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                return await ReadBoundedAsync(stream, url, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(
                    ex,
                    "Fetch attempt {Attempt}/{Max} for {Url} failed: {Message}",
                    attempt,
                    _maxAttempts,
                    url,
                    ex.Message);

                if (attempt >= _maxAttempts)
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
            $"Failed to fetch '{url}' after {_maxAttempts} attempts.",
            lastError);
    }

    // Reads the stream into memory but aborts the moment the byte count would
    // exceed the cap, so a server that lies about (or omits) Content-Length
    // still cannot exhaust memory.
    private async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        string url,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _maxBytes)
                throw new HttpRequestException(
                    $"Response for '{url}' exceeded the {_maxBytes}-byte cap.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
