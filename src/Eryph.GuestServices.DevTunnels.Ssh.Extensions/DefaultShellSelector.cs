namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

/// <summary>
/// Selector that does not consult Hyper-V KVP state. Used when the Extensions
/// library is consumed without the guest service (e.g. tests). Honors the
/// SSH-sent <c>SHELL</c>/<c>SHELL_ARGS</c> env vars and falls back to platform
/// defaults.
/// </summary>
public sealed class DefaultShellSelector : IShellSelector
{
    public static DefaultShellSelector Instance { get; } = new();

    public Task<ShellSelection> SelectAsync(
        ShellOverride sshOverride,
        CancellationToken cancellation)
    {
        return Task.FromResult(SelectFromEnvOrDefault(sshOverride));
    }

    /// <summary>
    /// Picks a shell from the SSH-sent override, falling back to the platform
    /// default. Exposed so service-side selectors can reuse the fallback chain
    /// after their own KVP lookup misses.
    /// </summary>
    public static ShellSelection SelectFromEnvOrDefault(ShellOverride sshOverride)
    {
        // Trim defensively — SSH env values come from a client we authenticate
        // but don't otherwise constrain. A stray trailing space would silently
        // fail process-start with a path-not-found-style error.
        var command = sshOverride.Command?.Trim();
        if (!string.IsNullOrEmpty(command))
            return new ShellSelection(command, sshOverride.Arguments?.Trim() ?? string.Empty);

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
