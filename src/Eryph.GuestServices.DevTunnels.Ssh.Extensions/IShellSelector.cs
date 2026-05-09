namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

/// <summary>
/// Resolves the command + arguments to spawn for an interactive SSH session.
/// </summary>
public interface IShellSelector
{
    /// <summary>
    /// Selects the shell for one session. The <paramref name="sessionEnvironment"/>
    /// contains environment variables sent by the SSH client via <c>env</c>
    /// channel requests on the same channel.
    /// </summary>
    Task<ShellSelection> SelectAsync(
        IReadOnlyDictionary<string, string> sessionEnvironment,
        CancellationToken cancellation);
}

/// <summary>
/// The command and arguments to spawn as the interactive shell.
/// </summary>
/// <param name="Command">Executable name or absolute path. Bare names are
/// resolved via <c>PATH</c>.</param>
/// <param name="Arguments">Argument string passed to the shell, may be empty.</param>
public sealed record ShellSelection(string Command, string Arguments);
