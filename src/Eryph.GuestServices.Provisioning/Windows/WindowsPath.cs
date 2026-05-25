namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Deterministic, host-OS-independent Windows path helpers. The provisioning
/// agent runs on Windows but builds and reasons about Windows paths
/// (drive-letter rooted, <c>\</c>-separated). <see cref="System.IO.Path"/>
/// applies the *host* OS's separator semantics, so on a Linux build/test
/// agent <c>Path.GetDirectoryName(@"C:\etc\foo")</c> returns <c>""</c> and
/// <c>Path.Combine</c> joins with <c>/</c> — both wrong for the Windows paths
/// the modules manipulate.
/// </summary>
/// <remarks>
/// This type implements the Windows semantics directly so the modules behave
/// identically regardless of the host the code runs (or is tested) on. It is
/// intentionally narrow: it provides only the operations the modules and
/// <see cref="WindowsOs.TranslateUnixPath"/> need. <c>/</c> is tolerated as a
/// separator on input (cloud-init authors mix them) but output always uses
/// <c>\</c>, matching <c>System.IO.Path</c>'s behaviour on Windows.
/// </remarks>
internal static class WindowsPath
{
    private const char Separator = '\\';

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>
    /// Returns the directory portion of a Windows path, matching
    /// <c>System.IO.Path.GetDirectoryName</c>'s behaviour on Windows:
    /// <c>@"C:\etc\foo"</c> → <c>@"C:\etc"</c>, <c>@"C:\foo"</c> → <c>@"C:\"</c>,
    /// a bare root (<c>@"C:\"</c>) or a path with no parent → <c>null</c>.
    /// </summary>
    public static string? GetDirectoryName(string? windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath))
            return null;

        var path = windowsPath;
        var rootLength = GetRootLength(path);

        // Trim trailing separators that are NOT part of the root so a path
        // like @"C:\etc\foo\" behaves like @"C:\etc\foo".
        var end = path.Length;
        while (end > rootLength && IsSeparator(path[end - 1]))
            end--;

        // Walk back to the separator that precedes the last segment.
        var i = end;
        while (i > rootLength && !IsSeparator(path[i - 1]))
            i--;

        // Drop trailing separators between the parent and the last segment.
        while (i > rootLength && IsSeparator(path[i - 1]))
            i--;

        if (i <= 0)
            return null;

        // If the only thing left is the root (e.g. @"C:\") the directory of
        // @"C:\foo" is the root itself — System.IO.Path returns @"C:\".
        if (i < rootLength)
            i = rootLength;

        // No parent beyond the root: @"C:\" has no directory name.
        if (i == rootLength && rootLength == path.Length)
            return null;

        var result = path[..i];
        return result.Length == 0 ? null : NormalizeSeparators(result);
    }

    /// <summary>
    /// Joins path parts with <c>\</c>. A part that is itself rooted (drive- or
    /// UNC-rooted) discards everything accumulated so far, matching
    /// <c>System.IO.Path.Combine</c> on Windows. Empty parts are skipped.
    /// </summary>
    public static string Combine(params string[] parts)
    {
        var result = string.Empty;
        foreach (var raw in parts)
        {
            if (string.IsNullOrEmpty(raw))
                continue;

            var part = raw;
            if (result.Length == 0 || IsRooted(part))
            {
                result = part;
                continue;
            }

            if (!IsSeparator(result[^1]))
                result += Separator;
            result += part;
        }

        return NormalizeSeparators(result);
    }

    /// <summary>
    /// Canonicalizes a drive-rooted or UNC Windows path against its own root
    /// using <c>\</c> semantics, WITHOUT consulting the host OS or current
    /// working directory (which <c>System.IO.Path.GetFullPath</c> does on
    /// non-Windows hosts). Resolves <c>.</c> segments and collapses redundant
    /// separators; <c>..</c> segments are resolved against the root and never
    /// allowed to escape it. Callers that must reject <c>..</c> outright should
    /// do so before calling this (see <see cref="WindowsOs.TranslateUnixPath"/>).
    /// </summary>
    public static string GetFullPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath))
            throw new ArgumentException("Path is empty.", nameof(windowsPath));

        var path = NormalizeSeparators(windowsPath);

        // UNC: \\server\share\... — keep the \\server\share root intact and
        // canonicalize the remainder beneath it.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return CanonicalizeUnc(path);

        var rootLength = GetDriveRootLength(path);
        if (rootLength == 0)
            throw new ArgumentException(
                $"Path '{windowsPath}' is not drive-rooted.", nameof(windowsPath));

        var root = path[..rootLength]; // e.g. "C:\"
        var rest = path[rootLength..];
        var segments = ResolveSegments(rest);
        return root + string.Join(Separator, segments);
    }

    private static string CanonicalizeUnc(string path)
    {
        // path starts with "\\". Split into the \\server\share prefix and the
        // remainder. We keep the first two non-empty components as the share root.
        var afterPrefix = path[2..];
        var comps = afterPrefix.Split(Separator, StringSplitOptions.None);
        var rootComps = new List<string>();
        var index = 0;
        for (; index < comps.Length && rootComps.Count < 2; index++)
        {
            if (comps[index].Length == 0)
                continue;
            rootComps.Add(comps[index]);
        }

        var root = @"\\" + string.Join(Separator, rootComps);
        var rest = index < comps.Length ? string.Join(Separator, comps[index..]) : string.Empty;
        var segments = ResolveSegments(rest);
        return segments.Count == 0 ? root : root + Separator + string.Join(Separator, segments);
    }

    private static List<string> ResolveSegments(string rest)
    {
        var result = new List<string>();
        foreach (var segment in rest.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                // Resolve against the root: never pop below it.
                if (result.Count > 0)
                    result.RemoveAt(result.Count - 1);
                continue;
            }
            result.Add(segment);
        }
        return result;
    }

    /// <summary>
    /// True when the path begins with a drive root (<c>X:\</c> / <c>X:/</c>) or
    /// a UNC root (<c>\\</c>).
    /// </summary>
    public static bool IsRooted(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        if (path.Length >= 2 && path[1] == ':')
            return true;
        return path[0] == '\\' || path[0] == '/';
    }

    private static string NormalizeSeparators(string path) => path.Replace('/', Separator);

    // Length of the root portion (incl. trailing separator if present), used
    // by GetDirectoryName so the root is never trimmed away.
    private static int GetRootLength(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            // UNC: treat \\server\share as the root for directory purposes.
            var i = 2;
            var sepsSeen = 0;
            for (; i < path.Length; i++)
            {
                if (IsSeparator(path[i]))
                {
                    sepsSeen++;
                    if (sepsSeen == 2)
                        break;
                }
            }
            return i;
        }

        if (path.Length >= 2 && path[1] == ':')
            return path.Length >= 3 && IsSeparator(path[2]) ? 3 : 2;

        return 0;
    }

    // Length of a drive root including the trailing separator (e.g. "C:\" → 3).
    private static int GetDriveRootLength(string path)
    {
        if (path.Length >= 3 && path[1] == ':' && IsSeparator(path[2]))
            return 3;
        return 0;
    }
}
