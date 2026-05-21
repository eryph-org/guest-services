using System.Text;
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
        var text = Encoding.UTF8.GetString(part.Body);
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

    private static List<string> ParseUrls(string content)
    {
        var urls = new List<string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue;
            urls.Add(line);
        }
        return urls;
    }
}
