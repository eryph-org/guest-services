using System.Text;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData.Handlers;

// Hand-rolled RFC 2046 multipart parser. Cloud-init userdata multiparts are
// extremely simple in practice (no nested multiparts, no quoted-printable
// transfer encoding, base64 only for binary), so we deliberately avoid
// pulling in MimeKit just for this. The structure is:
//
//   headers (Content-Type with boundary, MIME-Version, ...)
//   blank line
//   --boundary
//   per-part headers
//   blank line
//   body
//   --boundary
//   ...
//   --boundary--
//
// We re-emit each non-multipart part back into the pipeline. The pipeline
// will sniff or trust the per-part Content-Type and dispatch accordingly.
internal sealed class MultipartMimeHandler(ILogger<MultipartMimeHandler> logger) : IUserDataHandler
{
    public bool CanHandle(UserDataPart part) =>
        part.ContentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);

    public async Task ProcessAsync(
        UserDataPart part,
        IUserDataResolutionContext ctx,
        CancellationToken cancellationToken)
    {
        // BOM-stripping decode — a leading U+FEFF before "Content-Type:"
        // breaks header-line detection in the (lenient) MIME parser below.
        var text = UserDataEncoding.DecodeUtf8(part.Body);
        var parsed = MimeParser.Parse(text);
        if (parsed is null)
        {
            logger.LogWarning("Could not parse multipart user-data; ignoring");
            return;
        }

        foreach (var child in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contentType = child.ContentType ?? UserDataContentTypeSniffer.PlainText;
            // Strip charset/parameters; e.g. "text/x-shellscript; charset=us-ascii".
            var semi = contentType.IndexOf(';');
            if (semi >= 0) contentType = contentType[..semi].Trim();
            contentType = NormaliseContentType(contentType);

            var bodyBytes = DecodeTransferEncoding(child.Body, child.TransferEncoding);

            if (contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            {
                // Nested multipart — recurse.
                var nested = new UserDataPart(contentType, bodyBytes, child.Filename, child.Headers);
                await ctx.ProcessNestedAsync(nested, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // If the inner Content-Type is generic (e.g. application/octet-stream)
            // or absent, sniff from the body. Otherwise keep the declared one.
            if (contentType is "text/plain" or "application/octet-stream" or "")
            {
                contentType = UserDataContentTypeSniffer.Sniff(bodyBytes);
            }

            var partOut = new UserDataPart(contentType, bodyBytes, child.Filename, child.Headers);
            await ctx.ProcessNestedAsync(partOut, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string NormaliseContentType(string contentType)
    {
        // Cloud-init historically uses both text/cloud-config and text/x-cloud-config.
        if (contentType.Equals("text/cloud-config", StringComparison.OrdinalIgnoreCase))
            return UserDataContentTypeSniffer.CloudConfig;
        return contentType;
    }

    private static byte[] DecodeTransferEncoding(string body, string? encoding)
    {
        if (encoding is null)
            return Encoding.UTF8.GetBytes(body);

        if (encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Base64 bodies may have inline whitespace/CRLF.
                var clean = new StringBuilder(body.Length);
                foreach (var c in body)
                {
                    if (!char.IsWhiteSpace(c)) clean.Append(c);
                }
                return Convert.FromBase64String(clean.ToString());
            }
            catch
            {
                return Encoding.UTF8.GetBytes(body);
            }
        }

        // 7bit / 8bit / binary / quoted-printable — for v1 we treat anything we
        // don't explicitly decode as the raw UTF-8 bytes of the body. Real-world
        // cloud-init multiparts almost always use 7bit/8bit so the bytes round-trip.
        return Encoding.UTF8.GetBytes(body);
    }

    // ----------------------------------------------------------------------
    // RFC 2046 / RFC 5322 mini-parser.
    // ----------------------------------------------------------------------
    private sealed record MimeChild(
        string? ContentType,
        string? Filename,
        string? TransferEncoding,
        string Body,
        IReadOnlyDictionary<string, string> Headers);

    private static class MimeParser
    {
        public static IReadOnlyList<MimeChild>? Parse(string raw)
        {
            // Normalise CRLF -> LF for parsing; we keep the canonical form
            // internally and only need to identify boundary lines and headers.
            var text = raw.Replace("\r\n", "\n");

            var (rootHeaders, rootBodyStart) = ReadHeaders(text, 0);
            if (rootBodyStart < 0) return null;

            if (!rootHeaders.TryGetValue("content-type", out var ct)) return null;
            var boundary = ExtractBoundary(ct);
            if (boundary is null) return null;

            var openDelim = "--" + boundary;
            var closeDelim = openDelim + "--";

            var body = text[rootBodyStart..];
            var lines = body.Split('\n');

            var children = new List<MimeChild>();
            var collecting = false;
            var currentLines = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed == closeDelim)
                {
                    if (collecting) FlushChild(children, currentLines);
                    collecting = false;
                    break;
                }
                if (trimmed == openDelim)
                {
                    if (collecting) FlushChild(children, currentLines);
                    collecting = true;
                    currentLines = [];
                    continue;
                }
                if (collecting) currentLines.Add(line);
            }

            // RFC 2046 mandates a `--boundary--` close delimiter, but real
            // producers (eryph-zero's configdrive among them) sometimes omit it.
            // If the stream ends with an open part still being collected, flush
            // it — otherwise the LAST cloud-config / shellscript part is
            // silently dropped.
            if (collecting) FlushChild(children, currentLines);

            return children;

            static void FlushChild(List<MimeChild> sink, List<string> lines)
            {
                var partText = string.Join('\n', lines);
                var (headers, bodyStart) = ReadHeaders(partText, 0);
                if (bodyStart < 0)
                {
                    sink.Add(new MimeChild(null, null, null, partText, headers));
                    return;
                }

                var partBody = partText[bodyStart..];
                // Trim the trailing newline introduced by the join.
                if (partBody.EndsWith('\n')) partBody = partBody[..^1];

                headers.TryGetValue("content-type", out var contentType);
                headers.TryGetValue("content-transfer-encoding", out var transferEncoding);

                string? filename = null;
                if (headers.TryGetValue("content-disposition", out var disp))
                    filename = ExtractParameter(disp, "filename");
                if (filename is null && contentType is not null)
                    filename = ExtractParameter(contentType, "name");

                sink.Add(new MimeChild(contentType, filename, transferEncoding, partBody, headers));
            }
        }

        // Returns (headerMap, bodyStartIndex). bodyStartIndex is -1 if no body separator was found.
        private static (Dictionary<string, string>, int) ReadHeaders(string text, int start)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var idx = start;
            string? currentKey = null;
            var currentValue = new StringBuilder();
            var bodyStart = -1;

            while (idx < text.Length)
            {
                var lineEnd = text.IndexOf('\n', idx);
                var line = lineEnd < 0 ? text[idx..] : text[idx..lineEnd];
                line = line.TrimEnd('\r');

                if (line.Length == 0)
                {
                    if (currentKey is not null)
                    {
                        headers[currentKey] = currentValue.ToString();
                        currentKey = null;
                        currentValue.Clear();
                    }
                    bodyStart = lineEnd < 0 ? text.Length : lineEnd + 1;
                    break;
                }

                if (line[0] is ' ' or '\t')
                {
                    if (currentKey is not null)
                    {
                        currentValue.Append(' ');
                        currentValue.Append(line.TrimStart());
                    }
                }
                else
                {
                    if (currentKey is not null)
                    {
                        headers[currentKey] = currentValue.ToString();
                        currentValue.Clear();
                    }

                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        // Not a header line — treat everything from here as body.
                        bodyStart = idx;
                        return (headers, bodyStart);
                    }

                    currentKey = line[..colon].Trim().ToLowerInvariant();
                    currentValue.Append(line[(colon + 1)..].Trim());
                }

                if (lineEnd < 0) break;
                idx = lineEnd + 1;
            }

            if (currentKey is not null)
                headers[currentKey] = currentValue.ToString();

            return (headers, bodyStart);
        }

        private static string? ExtractBoundary(string contentType) =>
            ExtractParameter(contentType, "boundary");

        private static string? ExtractParameter(string headerValue, string parameterName)
        {
            foreach (var rawPart in headerValue.Split(';'))
            {
                var part = rawPart.Trim();
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part[..eq].Trim();
                if (!key.Equals(parameterName, StringComparison.OrdinalIgnoreCase)) continue;
                var value = part[(eq + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                return value;
            }
            return null;
        }
    }
}
