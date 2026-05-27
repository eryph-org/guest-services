using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources.OpenStack;

/// <summary>
/// HTTP client for the OpenStack metadata service on the link-local
/// <c>169.254.169.254</c> endpoint. cloud-init parity:
/// <c>DataSourceOpenStack</c> + <c>helpers/openstack.py MetadataReader</c>.
///
/// Policy (spec §7): no auth / no special headers; absent optional files return
/// 404 and are tolerated (surfaced as <c>null</c>); retry only on connection
/// errors and HTTP 408/429/5xx; fail fast on other 4xx.
/// </summary>
internal sealed class OpenStackMetadataClient : IDisposable
{
    internal const string DefaultBaseUrl = "http://169.254.169.254";
    internal const string VersionListPath = "openstack";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private const int DefaultMaxAttempts = 3;

    private static readonly HashSet<HttpStatusCode> RetryableStatuses =
    [
        HttpStatusCode.RequestTimeout,        // 408
        HttpStatusCode.TooManyRequests,       // 429
        HttpStatusCode.InternalServerError,   // 500
        HttpStatusCode.BadGateway,            // 502
        HttpStatusCode.ServiceUnavailable,    // 503
        HttpStatusCode.GatewayTimeout,        // 504
    ];

    private readonly HttpMessageHandler _handler;
    private readonly bool _disposeHandler;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;
    private readonly Func<int, CancellationToken, Task> _retryDelay;
    private readonly ILogger _logger;

    /// <summary>Production constructor: builds its own handler against the
    /// link-local endpoint. Proxy use is disabled — the metadata service lives on
    /// the link-local address 169.254.169.254, which must be reached directly; a
    /// system/WPAD proxy would break reachability and could leak metadata requests
    /// to an outbound proxy. Mirrors cloud-init / cloudbase-init, which bypass the
    /// proxy for IMDS.</summary>
    public OpenStackMetadataClient(ILogger logger)
        : this(
            new HttpClientHandler { UseProxy = false, Proxy = null },
            disposeHandler: true,
            DefaultBaseUrl,
            DefaultTimeout,
            DefaultMaxAttempts,
            retryDelay: (_, ct) => Task.Delay(TimeSpan.FromSeconds(1), ct),
            logger)
    {
    }

    /// <summary>Test seam: substituted handler, base URL, and (typically no-op)
    /// retry delay.</summary>
    internal OpenStackMetadataClient(
        HttpMessageHandler handler,
        bool disposeHandler,
        string baseUrl,
        TimeSpan timeout,
        int maxAttempts,
        Func<int, CancellationToken, Task> retryDelay,
        ILogger logger)
    {
        _handler = handler;
        _disposeHandler = disposeHandler;
        _baseUrl = baseUrl.TrimEnd('/');
        _timeout = timeout;
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelay = retryDelay;
        _logger = logger;
    }

    public string BaseUrl => _baseUrl;

    public string UrlFor(string relativePath) => $"{_baseUrl}/{relativePath.TrimStart('/')}";

    /// <summary>
    /// Liveness probe: <c>GET /openstack</c> (the version listing endpoint).
    /// Returns true when the service answers 2xx — the signal cloud-init uses to
    /// select the metadata service. Connection failures / non-2xx return false so
    /// the caller can back off and retry while the link-local network comes up.
    /// </summary>
    public async Task<bool> ProbeLivenessAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await GetAsync(VersionListPath, cancellationToken).ConfigureAwait(false);
            return bytes is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OpenStack metadata service liveness probe failed");
            return false;
        }
    }

    /// <summary>
    /// Fetches <paramref name="relativePath"/>. Returns the body bytes on 200,
    /// <c>null</c> on 404 (absent — tolerated), and throws after exhausting
    /// retries on transient failures or immediately on a non-retryable 4xx.
    /// </summary>
    public Task<byte[]?> GetAsync(string relativePath, CancellationToken cancellationToken)
    {
        return SendWithRetryAsync(relativePath, cancellationToken);
    }

    private async Task<byte[]?> SendWithRetryAsync(string relativePath, CancellationToken cancellationToken)
    {
        var url = UrlFor(relativePath);
        Exception? lastError = null;

        using var client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = _timeout,
        };

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Connection errors (and timeouts surfaced as TaskCanceled without
                // cancellation requested) are retryable.
                lastError = ex;
                _logger.LogDebug(
                    ex,
                    "OpenStack metadata GET {Url} attempt {Attempt}/{Max} failed: {Message}",
                    url, attempt, _maxAttempts, ex.Message);
            }

            // Status-code handling lives outside the catch so a fail-fast 4xx
            // propagates directly rather than being swallowed as "retryable".
            if (response is not null)
            {
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return null;

                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content
                            .ReadAsByteArrayAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }

                    // Non-retryable 4xx (400/401/403/…): fail fast, no retry.
                    if (!RetryableStatuses.Contains(response.StatusCode))
                    {
                        throw new HttpRequestException(
                            $"OpenStack metadata GET {url} returned HTTP {(int)response.StatusCode} {response.StatusCode}");
                    }

                    lastError = new HttpRequestException(
                        $"OpenStack metadata GET {url} returned retryable HTTP {(int)response.StatusCode} {response.StatusCode}");
                    _logger.LogDebug(
                        "OpenStack metadata GET {Url} attempt {Attempt}/{Max}: HTTP {Status}",
                        url, attempt, _maxAttempts, (int)response.StatusCode);
                }
            }

            if (attempt < _maxAttempts)
                await _retryDelay(attempt, cancellationToken).ConfigureAwait(false);
        }

        throw new HttpRequestException(
            $"OpenStack metadata GET {url} failed after {_maxAttempts} attempts", lastError);
    }

    public void Dispose()
    {
        if (_disposeHandler)
            _handler.Dispose();
    }
}
