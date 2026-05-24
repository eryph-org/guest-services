namespace Eryph.GuestServices.Provisioning.Windows;

public enum PowerStateAction
{
    Reboot = 0,
    Poweroff = 1,
    /// <summary>
    /// Cloud-init's <c>halt</c> on Linux stops the CPU without powering off.
    /// Windows has no clean analogue; the module falls back to hibernate
    /// and logs a Warning when this is requested. Kept as a distinct enum
    /// value so the OS layer can act on the intent.
    /// </summary>
    Halt = 2,
}

/// <summary>
/// Resolved power-state request passed to <see cref="IWindowsOs.RequestPowerStateAsync"/>.
/// Cloud-init delays / messages / conditions are normalised before this record is built.
/// </summary>
public sealed record PowerStateRequest
{
    public PowerStateAction Action { get; init; }

    /// <summary>
    /// Seconds from now. The OS layer forwards this verbatim to
    /// <c>shutdown.exe /t</c>. The module enforces a minimum buffer
    /// (typically 5s) so the StageRunner has time to flush its semaphores
    /// before Windows starts tearing down the agent process.
    /// </summary>
    public int DelaySeconds { get; init; }

    /// <summary>
    /// Operator-visible message. Truncated to 512 chars before being
    /// passed to <c>shutdown.exe /c</c>.
    /// </summary>
    public string? Message { get; init; }
}
