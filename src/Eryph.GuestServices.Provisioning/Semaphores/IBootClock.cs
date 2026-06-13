namespace Eryph.GuestServices.Provisioning.Semaphores;

/// <summary>
/// Exposes a "boot identifier" that changes whenever the host reboots.
/// Pulled out behind an interface so <see cref="BootSessionDetector"/> can
/// be exercised with a fake clock in unit tests; the production
/// implementation reads <c>Win32_OperatingSystem.LastBootUpTime</c>.
/// </summary>
public interface IBootClock
{
    /// <summary>
    /// Returns a stable, comparable identifier for the current boot. Two
    /// calls within the same boot return the same value; two calls across
    /// a reboot return different values.
    /// </summary>
    string GetCurrentBootId();

    /// <summary>
    /// Returns the time the machine last booted (UTC). Stable within a boot,
    /// later after a reboot. Used as the cloud-init "incarnation" — its epoch
    /// second matches cloud-init's <c>int(time.time() - uptime)</c>.
    /// </summary>
    DateTimeOffset GetCurrentBootTime();
}
