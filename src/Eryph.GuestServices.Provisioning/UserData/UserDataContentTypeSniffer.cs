using System.Text;

namespace Eryph.GuestServices.Provisioning.UserData;

// Sniffs the cloud-init content-type from raw bytes by inspecting the
// leading shebang / marker line. Used both at pipeline entry (to classify
// the root user-data blob) and inside handlers (e.g. when an include URL
// fetches another payload whose type we must classify the same way).
internal static class UserDataContentTypeSniffer
{
    public const string CloudConfig = "text/x-cloud-config";
    public const string IncludeUrl = "text/x-include-url";
    public const string ShellScript = "text/x-shellscript";
    public const string Boothook = "text/cloud-boothook";
    public const string MultipartMixed = "multipart/mixed";
    public const string PlainText = "text/plain";

    private static readonly byte[] GzipMagic = [0x1f, 0x8b];

    // Inspects up to the first non-empty line; recognises:
    //   #cloud-config           -> text/x-cloud-config
    //   #include / #include-once -> text/x-include-url
    //   #cloud-boothook         -> text/cloud-boothook
    //   #!/bin/sh, #!/bin/bash, generic #!  -> text/x-shellscript (kind: shell or other)
    //   #ps1, #ps1_sysnative    -> text/x-shellscript (kind: powershell)
    //   "Content-Type: multipart/", "MIME-Version:" or starts with a boundary
    //                           -> multipart/mixed (the multipart handler
    //                              will inspect the real subtype itself)
    // Anything else falls back to text/plain.
    public static string Sniff(byte[] body)
    {
        if (body is null || body.Length == 0)
            return PlainText;

        // Gzipped bytes are NOT a content-type; callers should decompress first.
        // If we ever see them at the sniff layer treat them as opaque text/plain
        // (they will simply be ignored with a warning by the pipeline).
        if (IsGzipped(body))
            return PlainText;

        var text = SafeDecode(body);
        var firstLine = FirstNonEmptyLine(text);
        if (firstLine.Length == 0)
            return PlainText;

        // Cloud-init's userdata MIME messages — and the configdrive ISO that
        // eryph-zero generates — are prefixed with an mbox-style "From " line
        // (RFC 4155 preamble) before the actual MIME headers. Skip it so the
        // Content-Type / MIME-Version detection below still triggers.
        //   From nobody Fri Jan  11 07:00:00 1980
        //   Content-Type: multipart/mixed; boundary="==BOUNDARY=="
        //   MIME-Version: 1.0
        if (firstLine.StartsWith("From ", StringComparison.Ordinal))
            firstLine = FirstNonEmptyLine(SkipFirstLine(text));

        // Multipart detection: either a MIME header is present at the very top,
        // or a "Content-Type: multipart/" header appears anywhere in the leading
        // block of the document. We keep the heuristic lenient because real
        // user-data sometimes emits "MIME-Version" before "Content-Type".
        if (firstLine.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)
            || firstLine.StartsWith("MIME-Version:", StringComparison.OrdinalIgnoreCase))
        {
            return MultipartMixed;
        }

        if (firstLine.StartsWith("#cloud-config", StringComparison.Ordinal))
            return CloudConfig;

        if (firstLine.StartsWith("#include-once", StringComparison.Ordinal)
            || firstLine.StartsWith("#include", StringComparison.Ordinal))
            return IncludeUrl;

        if (firstLine.StartsWith("#cloud-boothook", StringComparison.Ordinal))
            return Boothook;

        if (firstLine.StartsWith("#ps1_sysnative", StringComparison.Ordinal)
            || firstLine.StartsWith("#ps1", StringComparison.Ordinal)
            || firstLine.StartsWith("#!", StringComparison.Ordinal))
            return ShellScript;

        return PlainText;
    }

    public static ScriptKind DetectScriptKind(byte[] body)
    {
        var firstLine = FirstNonEmptyLine(SafeDecode(body));
        if (firstLine.StartsWith("#ps1", StringComparison.Ordinal))
            return ScriptKind.PowerShell;
        if (firstLine.StartsWith("#!/bin/sh", StringComparison.Ordinal)
            || firstLine.StartsWith("#!/bin/bash", StringComparison.Ordinal)
            || firstLine.StartsWith("#!/usr/bin/env bash", StringComparison.Ordinal))
            return ScriptKind.ShellScript;
        if (firstLine.StartsWith("#!", StringComparison.Ordinal))
            return ScriptKind.Other;
        return ScriptKind.Other;
    }

    public static bool IsGzipped(byte[] body) =>
        body.Length >= GzipMagic.Length
        && body[0] == GzipMagic[0]
        && body[1] == GzipMagic[1];

    public static byte[] DecompressIfGzipped(byte[] body)
    {
        if (!IsGzipped(body))
            return body;

        using var input = new MemoryStream(body);
        using var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static string SafeDecode(byte[] body)
    {
        try
        {
            var offset = 0;
            // Tools that write files on Windows (PowerShell Set-Content,
            // Notepad, etc.) commonly emit a UTF-8 BOM. Cloud-init's markers
            // (#cloud-config, #!/bin/sh, etc.) MUST be the first non-whitespace
            // bytes, so strip the BOM before reading the leading line.
            if (body.Length >= Utf8Bom.Length
                && body[0] == Utf8Bom[0]
                && body[1] == Utf8Bom[1]
                && body[2] == Utf8Bom[2])
            {
                offset = Utf8Bom.Length;
            }

            // Take at most the first 4KB; we only need the leading line.
            var len = Math.Min(body.Length - offset, 4096);
            return Encoding.UTF8.GetString(body, offset, len);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SkipFirstLine(string text)
    {
        var nl = text.IndexOf('\n');
        return nl < 0 ? string.Empty : text[(nl + 1)..];
    }

    private static string FirstNonEmptyLine(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var lineEnd = text.IndexOf('\n', index);
            var line = lineEnd < 0 ? text[index..] : text[index..lineEnd];
            line = line.TrimEnd('\r').Trim();
            if (line.Length > 0)
                return line;
            if (lineEnd < 0) break;
            index = lineEnd + 1;
        }
        return string.Empty;
    }
}
