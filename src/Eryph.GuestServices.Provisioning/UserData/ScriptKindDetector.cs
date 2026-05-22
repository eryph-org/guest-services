using System.Text;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData;

/// <summary>
/// Decides how a shell-script user-data part should be dispatched. Mirrors the
/// real-world cloudbase-init dispatch rules (filename-led, NOT shebang-led)
/// because the eryph gene corpus has been crafted around two cbi bugs:
/// parts without <c>filename=</c> are dropped, and cbi ignores shebangs.
/// See <c>docs/rfcs/0007-scripts-per-frequency-edge-cases.md</c> and the
/// <c>cbi-compat-constraints</c> memory.
/// </summary>
/// <remarks>
/// <para>Detection priority (Windows guest):</para>
/// <list type="number">
///   <item>filename extension (<c>.ps1</c>, <c>.cmd</c>/<c>.bat</c>; <c>.sh</c>
///         is recognised but resolved to <see cref="ScriptKind.Other"/> on
///         Windows with a warning since no POSIX shell is available).</item>
///   <item>shebang on the first non-empty body line (<c>#ps1_sysnative</c>,
///         <c>#ps1</c>; <c>#!/...</c> is recognised but resolved to
///         <see cref="ScriptKind.Other"/> on Windows for the same reason).</item>
///   <item>content-type parameter (<c>text/x-shellscript</c> falls back to
///         PowerShell on Windows with a warning logged).</item>
///   <item>otherwise <see cref="ScriptKind.Other"/> with a warning logged so
///         the operator can see the part was skipped — never a silent drop.</item>
/// </list>
/// </remarks>
internal static class ScriptKindDetector
{
    public static ScriptKind Detect(
        string? filename,
        byte[] body,
        string? contentType,
        ILogger logger)
    {
        // (1) filename-led — matches cbi's actual dispatch behavior. Eryph
        //     genes always include filename= because cbi drops parts that
        //     don't, and they typically omit the shebang because cbi ignores
        //     shebangs anyway. We MUST honor the extension first.
        var extension = TryGetExtension(filename);
        if (extension is not null)
        {
            switch (extension)
            {
                case ".ps1":
                    return ScriptKind.PowerShell;
                case ".cmd":
                case ".bat":
                    return ScriptKind.Cmd;
                case ".sh":
                    logger.LogWarning(
                        "Shell script '{Filename}' has a POSIX (.sh) extension; "
                        + "skipping on Windows — no POSIX shell is available.",
                        filename);
                    return ScriptKind.Other;
            }
        }

        // (2) shebang fallback — cloud-init's documented dispatch.
        var firstLine = FirstNonEmptyLine(body);
        if (firstLine.StartsWith("#ps1", StringComparison.Ordinal))
            return ScriptKind.PowerShell;
        if (firstLine.StartsWith("#!", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Shell script '{Filename}' has POSIX shebang '{Shebang}'; "
                + "skipping on Windows — no POSIX shell is available.",
                filename ?? "<root>",
                firstLine);
            return ScriptKind.Other;
        }

        // (3) content-type fallback — handles hand-written cloud-config that
        //     supplies neither a usable filename nor a shebang. cbi would
        //     reject this shape (no filename) but cloud-init-aware tooling
        //     might produce it; we accept it best-effort as PowerShell on
        //     Windows and log a warning so the operator notices.
        if (string.Equals(contentType, UserDataContentTypeSniffer.ShellScript, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Shell script '{Filename}' has no filename extension and no shebang; "
                + "falling back to PowerShell on Windows based on Content-Type.",
                filename ?? "<root>");
            return ScriptKind.PowerShell;
        }

        // (4) unknown — log so this isn't a silent drop. The first end-to-end
        //     run had a real gene part silently classified as Other; we want
        //     a warning trail when it happens again.
        logger.LogWarning(
            "Shell script '{Filename}' has no recognizable filename extension, "
            + "shebang, or content-type; skipping.",
            filename ?? "<root>");
        return ScriptKind.Other;
    }

    private static string? TryGetExtension(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;
        var ext = Path.GetExtension(filename);
        return string.IsNullOrEmpty(ext) ? null : ext.ToLowerInvariant();
    }

    private static string FirstNonEmptyLine(byte[] body)
    {
        if (body is null || body.Length == 0)
            return string.Empty;

        // Strip BOM and take at most the first 4KB.
        var offset = 0;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            offset = 3;
        var len = Math.Min(body.Length - offset, 4096);
        string text;
        try
        {
            text = Encoding.UTF8.GetString(body, offset, len);
        }
        catch
        {
            return string.Empty;
        }

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
