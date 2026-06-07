namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

/// <summary>
/// Resolves the command + arguments to spawn for an SSH session. The
/// interactive shell path uses both the command and the arguments; the
/// non-interactive <c>exec</c> path uses only the command (it supplies its own
/// command-mode flag and ignores the interactive arguments).
/// </summary>
public interface IShellSelector
{
    /// <summary>
    /// Selects the shell for one session. The <paramref name="sshOverride"/>
    /// carries any per-session preferences that were sent by the client via
    /// SSH <c>env</c> channel requests.
    /// </summary>
    Task<ShellSelection> SelectAsync(
        ShellOverride sshOverride,
        CancellationToken cancellation);
}

/// <summary>
/// The command and arguments to spawn as the interactive shell.
/// </summary>
/// <param name="Command">On Windows, an executable name or an absolute path —
/// bare names are resolved via <c>PATH</c> by <c>CreateProcess</c>. On Linux,
/// must be an absolute path; bare names will fail at PTY start.</param>
/// <param name="Arguments">Argument string passed to the shell, may be empty.</param>
public sealed record ShellSelection(string Command, string Arguments);

/// <summary>
/// Per-session shell preference conveyed by the SSH client via <c>env</c>
/// channel requests. Either field may be <see langword="null"/> if the client
/// did not set the corresponding env var. A blank <see cref="Command"/> means
/// "no preference" and the selector should fall back.
/// </summary>
public readonly record struct ShellOverride(string? Command, string? Arguments)
{
    public static ShellOverride Empty => default;
}
