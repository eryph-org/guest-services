using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources.Azure;

/// <summary>
/// Thin client around the Azure Instance Metadata Service. IMDS lives on the
/// link-local 169.254.169.254 endpoint and requires the
/// <c>Metadata: true</c> header (Azure rejects requests without it to prevent
/// SSRF-style abuse from inside the VM). See RFC 0014 for the full policy
/// (retry once on transient, 5s timeout, no auth on instance metadata).
/// </summary>
internal sealed class AzureImdsClient : IDisposable
{
    internal const string ImdsUrl =
        "http://169.254.169.254/metadata/instance?api-version=2021-02-01";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpMessageHandler _handler;
    private readonly bool _disposeHandler;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    /// <summary>
    /// Production constructor: builds its own <see cref="HttpClientHandler"/>.
    /// </summary>
    public AzureImdsClient(ILogger logger)
        : this(new HttpClientHandler(), disposeHandler: true, DefaultTimeout, logger)
    {
    }

    /// <summary>
    /// Test seam: caller supplies a substituted <see cref="HttpMessageHandler"/>
    /// (e.g. an NSubstitute mock or a fake delegating handler).
    /// </summary>
    internal AzureImdsClient(
        HttpMessageHandler handler,
        bool disposeHandler,
        TimeSpan timeout,
        ILogger logger)
    {
        _handler = handler;
        _disposeHandler = disposeHandler;
        _timeout = timeout;
        _logger = logger;
    }

    /// <summary>
    /// Performs one GET with one retry on transient failure. Returns null when
    /// IMDS is unreachable after both attempts — callers fall back to whatever
    /// they can scrape from the registry / ovf-env.
    /// </summary>
    public async Task<JsonDocument?> TryGetInstanceMetadataAsync(CancellationToken cancellationToken)
    {
        // One retry => two attempts total.
        const int maxAttempts = 2;
        Exception? lastError = null;

        using var client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = _timeout,
        };

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ImdsUrl);
                // BOTH headers are mandatory:
                //  - "Metadata: true" — IMDS rejects requests without it.
                //  - "Accept: application/json" — some intermediaries default to
                //    XML if the client doesn't constrain the response type.
                request.Headers.Add("Metadata", "true");
                request.Headers.Add("Accept", "application/json");

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException(
                        $"IMDS returned HTTP {(int)response.StatusCode} {response.StatusCode}");
                    _logger.LogDebug(
                        "IMDS attempt {Attempt}/{Max}: HTTP {Status}",
                        attempt, maxAttempts, (int)response.StatusCode);
                }
                else
                {
                    var bytes = await response.Content
                        .ReadAsByteArrayAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return JsonDocument.Parse(bytes);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogDebug(
                    ex,
                    "IMDS attempt {Attempt}/{Max} failed: {Message}",
                    attempt, maxAttempts, ex.Message);
            }
        }

        _logger.LogInformation(
            lastError,
            "Azure IMDS unreachable after {Max} attempts; continuing without live instance metadata",
            maxAttempts);
        return null;
    }

    public void Dispose()
    {
        if (_disposeHandler)
            _handler.Dispose();
    }
}
