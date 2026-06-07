namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

/// <summary>
/// Maps a shell executable to the flag that makes it run a single command
/// string non-interactively. This is the SSH <c>exec</c> request equivalent of
/// OpenSSH's <c>$SHELL -c "&lt;command&gt;"</c> contract: the shell — not our
/// own tokenizer — parses pipes, quotes, redirection and globbing.
/// </summary>
public static class NonInteractiveShell
{
    /// <summary>
    /// Returns the command-mode flag for <paramref name="shellCommand"/>,
    /// inferred from its file name. PowerShell variants use <c>-Command</c>,
    /// <c>cmd</c> uses <c>/c</c>, and every other (POSIX) shell defaults to
    /// <c>-c</c>.
    /// </summary>
    public static string CommandFlagFor(string? shellCommand)
    {
        // Reduce "C:\Program Files\PowerShell\7\pwsh.exe" or "/bin/bash" to a
        // bare, extension-less, lower-case name. Split on both separators so a
        // Windows-style path is handled even when running on Linux.
        var trimmed = (shellCommand ?? string.Empty).Trim();
        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        var fileName = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        var dot = fileName.LastIndexOf('.');
        var name = (dot > 0 ? fileName[..dot] : fileName).ToLowerInvariant();

        return name switch
        {
            "pwsh" or "powershell" => "-Command",
            "cmd" => "/c",
            _ => "-c",
        };
    }
}
