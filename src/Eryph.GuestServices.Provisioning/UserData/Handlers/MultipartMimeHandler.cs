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
//
// The parser operates on raw bytes throughout — see review.md Finding 5 and
// memory feedback_binary_contracts. Headers are RFC 5322 US-ASCII (we decode
// them as Latin-1 to tolerate non-conforming producers); part bodies are
// sliced as byte ranges and only the base64 transfer encoding actually
// reinterprets the bytes. 7bit / 8bit / binary / unrecognised bodies are
// returned verbatim so that >0x7F bytes survive intact.
internal sealed class MultipartMimeHandler(ILogger<MultipartMimeHandler> logger) : IUserDataHandler
{
    public bool CanHandle(UserDataPart part) =>
        part.ContentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);

    public async Task ProcessAsync(
        UserDataPart part,
        IUserDataResolutionContext ctx,
        CancellationToken cancellationToken)
    {
        var parsed = MimeParser.Parse(part.Body);
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

    private static byte[] DecodeTransferEncoding(byte[] body, string? encoding)
    {
        if (encoding is null)
            return body;

        if (encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Base64 bodies may have inline ASCII whitespace/CRLF between
                // groups. Strip them at the byte level so the bytes never go
                // through a UTF-8 round-trip (a high-byte producer-error would
                // otherwise be silently turned into U+FFFD).
                var clean = new byte[body.Length];
                var w = 0;
                foreach (var b in body)
                {
                    if (b == 0x20 || b == 0x09 || b == 0x0A || b == 0x0D)
                        continue;
                    clean[w++] = b;
                }
                return Convert.FromBase64String(Encoding.ASCII.GetString(clean, 0, w));
            }
            catch
            {
                return body;
            }
        }

        // 7bit / 8bit / binary / quoted-printable / anything else — return the
        // bytes verbatim. We deliberately do NOT round-trip through UTF-8: an
        // 8bit-declared script with 0x80+ bytes would otherwise be mangled
        // before it reached the script handler. Quoted-printable is a known
        // pass-through gap documented in differences-from-cloud-init.md; real
        // cloud-init userdata multiparts effectively never use it.
        return body;
    }

    // ----------------------------------------------------------------------
    // RFC 2046 / RFC 5322 mini-parser.
    // ----------------------------------------------------------------------
    private sealed record MimeChild(
        string? ContentType,
        string? Filename,
        string? TransferEncoding,
        byte[] Body,
        IReadOnlyDictionary<string, string> Headers);

    private static class MimeParser
    {
        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        public static IReadOnlyList<MimeChild>? Parse(byte[] raw)
        {
            if (raw is null || raw.Length == 0)
                return null;

            // Strip a leading UTF-8 BOM at the byte level. Tools that write
            // files on Windows (PowerShell Set-Content, Notepad, ...) commonly
            // emit EF BB BF and the MIME header parser needs Content-Type to
            // be the very first bytes.
            var start = 0;
            if (raw.Length >= Utf8Bom.Length
                && raw[0] == Utf8Bom[0]
                && raw[1] == Utf8Bom[1]
                && raw[2] == Utf8Bom[2])
            {
                start = Utf8Bom.Length;
            }

            // Cloud-init's userdata MIME messages — and the configdrive ISO
            // that eryph-zero generates — are sometimes prefixed with an
            // mbox-style "From " line (RFC 4155 preamble) before the actual
            // MIME headers. Some producers emit "From:" with a colon, turning
            // the marker into a degenerate RFC 5322 header. We tolerate both
            // shapes by skipping the first line if it starts with either —
            // the next line should still be Content-Type / MIME-Version.
            start = SkipMboxPreamble(raw, start);

            var (rootHeaders, rootBodyStart) = ReadHeaders(raw, start);
            if (rootBodyStart < 0) return null;

            if (!rootHeaders.TryGetValue("content-type", out var ct)) return null;
            var boundary = ExtractBoundary(ct);
            if (boundary is null) return null;

            var openDelimAscii = "--" + boundary;
            var closeDelimAscii = openDelimAscii + "--";
            var openDelim = Encoding.ASCII.GetBytes(openDelimAscii);
            var closeDelim = Encoding.ASCII.GetBytes(closeDelimAscii);

            var children = new List<MimeChild>();
            var idx = rootBodyStart;
            var collectingStart = -1;
            var collectingEnd = -1;
            var inPart = false;

            while (idx < raw.Length)
            {
                // Find the start of the current line. We've already advanced
                // past any prior line terminator. The "boundary lines" we
                // care about are lines that are EXACTLY "--boundary" or
                // "--boundary--" (RFC 2046 allows trailing whitespace which
                // we tolerate via TrimAscii).
                var lineEnd = IndexOfLf(raw, idx);
                var lineLen = (lineEnd < 0 ? raw.Length : lineEnd) - idx;
                var bareLen = lineLen;
                if (bareLen > 0 && raw[idx + bareLen - 1] == (byte)'\r') bareLen--;

                if (IsBoundaryLine(raw, idx, bareLen, closeDelim))
                {
                    if (inPart)
                    {
                        // The boundary line itself is NOT part of the body,
                        // and the line terminator before it is the body's
                        // closing CRLF (RFC 2046). Trim it.
                        FlushChild(children, raw, collectingStart, TrimTrailingCrlf(raw, collectingStart, collectingEnd));
                    }
                    inPart = false;
                    break;
                }

                if (IsBoundaryLine(raw, idx, bareLen, openDelim))
                {
                    if (inPart)
                        FlushChild(children, raw, collectingStart, TrimTrailingCrlf(raw, collectingStart, collectingEnd));
                    inPart = true;
                    // Body of the next part starts on the next line.
                    collectingStart = lineEnd < 0 ? raw.Length : lineEnd + 1;
                    collectingEnd = collectingStart;
                    idx = collectingStart;
                    continue;
                }

                if (inPart)
                    collectingEnd = lineEnd < 0 ? raw.Length : lineEnd + 1;

                if (lineEnd < 0) break;
                idx = lineEnd + 1;
            }

            // RFC 2046 mandates a `--boundary--` close delimiter, but real
            // producers (eryph-zero's configdrive among them) sometimes omit
            // it. Flush the last open part at EOF so it isn't silently lost.
            if (inPart)
                FlushChild(children, raw, collectingStart, collectingEnd);

            return children;

            static void FlushChild(List<MimeChild> sink, byte[] raw, int from, int to)
            {
                if (from >= raw.Length || to <= from)
                {
                    sink.Add(new MimeChild(null, null, null, [], EmptyHeaders));
                    return;
                }

                var (headers, bodyStart) = ReadHeaders(raw, from, to);
                if (bodyStart < 0)
                {
                    var preBody = Slice(raw, from, to);
                    sink.Add(new MimeChild(null, null, null, preBody, headers));
                    return;
                }

                headers.TryGetValue("content-type", out var contentType);
                headers.TryGetValue("content-transfer-encoding", out var transferEncoding);

                string? filename = null;
                if (headers.TryGetValue("content-disposition", out var disp))
                    filename = ExtractParameter(disp, "filename");
                if (filename is null && contentType is not null)
                    filename = ExtractParameter(contentType, "name");

                var body = Slice(raw, bodyStart, to);
                sink.Add(new MimeChild(contentType, filename, transferEncoding, body, headers));
            }
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static int SkipMboxPreamble(byte[] raw, int start)
        {
            // Match "From " or "From:" at the very top of the document
            // (case-sensitive ASCII per the mbox / RFC 4155 convention).
            if (raw.Length - start < 5) return start;
            if (raw[start] != (byte)'F' || raw[start + 1] != (byte)'r'
                || raw[start + 2] != (byte)'o' || raw[start + 3] != (byte)'m'
                || (raw[start + 4] != (byte)' ' && raw[start + 4] != (byte)':'))
                return start;

            var lineEnd = IndexOfLf(raw, start);
            return lineEnd < 0 ? raw.Length : lineEnd + 1;
        }

        private static int IndexOfLf(byte[] raw, int from)
        {
            for (var i = from; i < raw.Length; i++)
                if (raw[i] == (byte)'\n') return i;
            return -1;
        }

        private static bool IsBoundaryLine(byte[] raw, int lineStart, int bareLen, byte[] delim)
        {
            if (bareLen != delim.Length) return false;
            for (var i = 0; i < delim.Length; i++)
                if (raw[lineStart + i] != delim[i]) return false;
            return true;
        }

        private static int TrimTrailingCrlf(byte[] raw, int from, int to)
        {
            // The boundary delimiter line "owns" the CRLF that PRECEDES it
            // (RFC 2046 §5.1.1). Strip one trailing LF and an optional CR so
            // we don't accidentally append the boundary's leading newline to
            // the previous part's body.
            if (to > from && raw[to - 1] == (byte)'\n') to--;
            if (to > from && raw[to - 1] == (byte)'\r') to--;
            return to;
        }

        private static byte[] Slice(byte[] raw, int from, int to)
        {
            if (to <= from) return [];
            var len = to - from;
            var dst = new byte[len];
            Buffer.BlockCopy(raw, from, dst, 0, len);
            return dst;
        }

        // Returns (headerMap, bodyStartIndex). bodyStartIndex is -1 if no
        // body separator was found inside [start, end).
        private static (Dictionary<string, string>, int) ReadHeaders(byte[] raw, int start, int? end = null)
        {
            var stop = end ?? raw.Length;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var idx = start;
            string? currentKey = null;
            var currentValue = new StringBuilder();
            var bodyStart = -1;

            while (idx < stop)
            {
                var lineEnd = idx;
                while (lineEnd < stop && raw[lineEnd] != (byte)'\n') lineEnd++;

                var bareLen = lineEnd - idx;
                if (bareLen > 0 && raw[idx + bareLen - 1] == (byte)'\r') bareLen--;

                // Decode header bytes as Latin-1 (ISO-8859-1). RFC 5322
                // requires US-ASCII; Latin-1 is the well-known safe
                // tolerant choice for arbitrary 0..0xFF bytes — every byte
                // maps to itself, so we never throw on non-ASCII in a
                // hand-rolled header.
                var line = Encoding.Latin1.GetString(raw, idx, bareLen);

                if (line.Length == 0)
                {
                    if (currentKey is not null)
                    {
                        headers[currentKey] = currentValue.ToString();
                        currentKey = null;
                        currentValue.Clear();
                    }
                    bodyStart = lineEnd >= stop ? stop : lineEnd + 1;
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

                if (lineEnd >= stop) break;
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
