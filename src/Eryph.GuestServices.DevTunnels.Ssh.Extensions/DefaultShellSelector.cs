using Eryph.GuestServices.Core;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

/// <summary>
/// Selector that does not consult Hyper-V KVP state. Used when the Extensions
/// library is consumed without the guest service (e.g. tests). Honors SSH-sent
/// <c>SHELL</c>/<c>SHELL_ARGS</c> env vars and falls back to platform defaults.
/// </summary>
public sealed class DefaultShellSelector : IShellSelector
{
    public static DefaultShellSelector Instance { get; } = new();

    public Task<ShellSelection> SelectAsync(
        IReadOnlyDictionary<string, string> sessionEnvironment,
        CancellationToken cancellation)
    {
        return Task.FromResult(SelectFromEnvOrDefault(sessionEnvironment));
    }

    /// <summary>
    /// Picks a shell from the SSH-sent environment, falling back to the
    /// platform default. Exposed so service-side selectors can reuse the
    /// fallback chain after their own KVP lookup misses.
    /// </summary>
    public static ShellSelection SelectFromEnvOrDefault(
        IReadOnlyDictionary<string, string> sessionEnvironment)
    {
        if (sessionEnvironment.TryGetValue(Constants.ShellEnvName, out var sshShell)
            && !string.IsNullOrWhiteSpace(sshShell))
        {
            sessionEnvironment.TryGetValue(Constants.ShellArgsEnvName, out var sshArgs);
            return new ShellSelection(sshShell, sshArgs ?? string.Empty);
        }

        return PlatformDefault();
    }

    /// <summary>
    /// The hardcoded platform default — last resort when no override is set
    /// anywhere.
    /// </summary>
    public static ShellSelection PlatformDefault()
    {
        if (OperatingSystem.IsLinux())
        {
            var processShell = Environment.GetEnvironmentVariable("SHELL");
            return new ShellSelection(
                string.IsNullOrEmpty(processShell) ? "/bin/bash" : processShell,
                "-i");
        }

        return new ShellSelection("powershell.exe", "-WindowStyle Hidden");
    }
}
