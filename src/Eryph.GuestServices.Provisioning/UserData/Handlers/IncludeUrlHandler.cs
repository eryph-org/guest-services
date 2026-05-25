using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData.Handlers;

// Handles #include / #include-once: each non-empty, non-comment line is
// fetched via IUrlHelper, gunzipped if needed, and re-introduced into the
// pipeline as a nested part. We tolerate per-URL failures (matching
// cloud-init's "best effort" semantics) and break #include cycles via the
// context's TryMarkVisited.
internal sealed class IncludeUrlHandler(
    IUrlHelper urlHelper,
    ILogger<IncludeUrlHandler> logger) : IUserDataHandler
{
    public bool CanHandle(UserDataPart part) =>
        part.ContentType.Equals(UserDataContentTypeSniffer.IncludeUrl, StringComparison.OrdinalIgnoreCase)
        || part.ContentType.Equals("text/x-include-once-url", StringComparison.OrdinalIgnoreCase);

    public async Task ProcessAsync(
        UserDataPart part,
        IUserDataResolutionContext ctx,
        CancellationToken cancellationToken)
    {
        // BOM-stripping decode — without it, a Windows-PowerShell-emitted
        // `﻿#include URL` line no longer starts with `#include`, the
        // marker check fails, and the inline URL is silently dropped.
        var text = UserDataEncoding.DecodeUtf8(part.Body);
        var urls = ParseUrls(text);

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ctx.TryMarkVisited(url))
            {
                logger.LogWarning("Skipping already-visited include URL: {Url}", url);
                continue;
            }

            byte[] payload;
            try
            {
                payload = await urlHelper.FetchAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Cloud-init tolerates per-URL failures; keep going so a partial
                // include set does not derail the entire pipeline.
                logger.LogWarning(ex, "Failed to fetch include URL {Url}; skipping", url);
                continue;
            }

            if (payload.Length == 0)
            {
                logger.LogWarning("Include URL {Url} returned empty body; skipping", url);
                continue;
            }

            var decoded = UserDataContentTypeSniffer.DecompressIfGzipped(payload);
            var sniffed = UserDataContentTypeSniffer.Sniff(decoded);

            if (sniffed == UserDataContentTypeSniffer.PlainText)
            {
                logger.LogWarning(
                    "Include URL {Url} returned content of unknown type; skipping",
                    url);
                continue;
            }

            // Use the URL as the filename so downstream consumers can trace
            // a captured script back to where it came from.
            var nested = new UserDataPart(sniffed, decoded, url);
            await ctx.ProcessNestedAsync(nested, cancellationToken).ConfigureAwait(false);
        }
    }

    // Recognised marker prefixes, longest first so `#include-once` wins over
    // `#include`. The sniffer classifies the part by the same prefixes (see
    // UserDataContentTypeSniffer); we never re-check the content-type here.
    private static readonly string[] IncludeMarkers = ["#include-once", "#include"];

    private static List<string> ParseUrls(string content)
    {
        // Mirrors cloud-init's per-line loop in cloudinit/user_data.py
        // (_do_include): every line gets its marker prefix stripped (if
        // present); plain URLs fall through unchanged; bare `#` lines are
        // comments. This handles all four shapes uniformly:
        //
        //   `#include`            → marker stripped, empty remainder, skipped
        //   `#include URL`        → marker stripped, URL collected
        //   `URL` (after marker)  → no prefix, collected directly
        //   `# any other comment` → stays `#…`, skipped by the `#` guard
        //
        // We diverge from cloud-init in one safe way: the marker MUST be
        // followed by whitespace or end-of-line, so `#includesomething`
        // does NOT mis-parse as marker `#include` + URL `something`.
        var urls = new List<string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            line = StripMarkerIfPresent(line);
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue;

            urls.Add(line);
        }
        return urls;
    }

    private static string StripMarkerIfPresent(string line)
    {
        foreach (var marker in IncludeMarkers)
        {
            if (!line.StartsWith(marker, StringComparison.Ordinal))
                continue;

            // Bare marker on its own line.
            if (line.Length == marker.Length)
                return string.Empty;

            // Marker MUST be whitespace-terminated; otherwise leave the
            // line untouched (the `#` guard above will then drop it).
            var next = line[marker.Length];
            if (next is not (' ' or '\t'))
                return line;

            return line[(marker.Length + 1)..].Trim();
        }
        return line;
    }
}
