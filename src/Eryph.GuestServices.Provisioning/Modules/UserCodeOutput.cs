namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Builds a compact, operator-facing tail of a user command's output for the
/// failure report. Both streams are included — a failing PowerShell script often
/// writes its error to stdout (Write-Host / Write-Output) while stderr carries
/// only a warning, so neither stream alone is reliable. When both are present
/// the budget is split so one noisy stream cannot crowd out the other, and each
/// stream's trailing slice is kept because the actual error is almost always the
/// last thing written before a non-zero exit.
/// </summary>
internal static class UserCodeOutput
{
    // ~1.5 KiB keeps a failure comfortably inside the cloud-init KVP value limit
    // plus at most a couple of break-down subkeys, so a typical stack trace
    // survives without flooding the pool.
    public const int DefaultMaxChars = 1500;

    private const string TruncationMarker = "…(truncated)\n";

    public static string Tail(string? stdout, string? stderr, int maxChars = DefaultMaxChars)
    {
        if (maxChars < 1)
            maxChars = 1;

        var err = (stderr ?? string.Empty).Trim();
        var outp = (stdout ?? string.Empty).Trim();
        if (err.Length == 0 && outp.Length == 0)
            return string.Empty;

        // Split the budget only when both streams carry content.
        var budget = err.Length > 0 && outp.Length > 0 ? Math.Max(1, maxChars / 2) : maxChars;

        var parts = new List<string>(2);
        if (err.Length > 0)
            parts.Add("stderr: " + Slice(err, budget));
        if (outp.Length > 0)
            parts.Add("stdout: " + Slice(outp, budget));

        return string.Join("\n", parts);
    }

    // Appends the output tail to a failure reason, if any output was captured.
    public static string AppendTail(string reason, string? stdout, string? stderr)
    {
        var tail = Tail(stdout, stderr);
        return tail.Length == 0 ? reason : $"{reason}\n{tail}";
    }

    private static string Slice(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;

        // Don't split a UTF-16 surrogate pair at the cut boundary.
        var start = text.Length - maxChars;
        if (char.IsLowSurrogate(text[start]) && start > 0)
            start--;

        return TruncationMarker + text[start..];
    }
}
