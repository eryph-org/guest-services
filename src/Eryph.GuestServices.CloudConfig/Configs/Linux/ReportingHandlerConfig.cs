namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>reporting:</c> per-handler entry. The top-level
/// <c>reporting</c> key carries a map of handler name → handler config.
/// Linux-only configuration; no-op on Windows today.
/// </summary>
[CloudInitRecord]
public sealed record ReportingHandlerConfig
{
    /// <summary>Handler type (e.g. <c>webhook</c>, <c>log</c>, <c>hyperv</c>).</summary>
    public string? Type { get; init; }

    /// <summary>Endpoint URL (webhook handler).</summary>
    public string? Endpoint { get; init; }

    /// <summary>OAuth consumer key (webhook handler).</summary>
    public string? ConsumerKey { get; init; }

    /// <summary>OAuth token key (webhook handler).</summary>
    public string? TokenKey { get; init; }

    /// <summary>OAuth consumer secret (webhook handler).</summary>
    public string? ConsumerSecret { get; init; }

    /// <summary>OAuth token secret (webhook handler).</summary>
    public string? TokenSecret { get; init; }

    /// <summary>Log level for the log handler.</summary>
    public string? Level { get; init; }

    /// <summary>HTTP headers (webhook handler).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>HTTP timeout in seconds (webhook handler).</summary>
    public int? Timeout { get; init; }

    /// <summary>HTTP retry count (webhook handler).</summary>
    public int? Retries { get; init; }
}
