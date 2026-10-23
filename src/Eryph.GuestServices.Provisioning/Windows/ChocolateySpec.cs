namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Outcome of a Chocolatey bootstrap. The community <c>install.ps1</c> may pull
/// in a .NET Framework that needs a reboot before <c>choco</c> is usable; the
/// module maps <see cref="RebootRequired"/> to
/// <c>ModuleOutcome.RebootRequested</c> (RFC 0021).
/// </summary>
public sealed record ChocolateyBootstrapResult
{
    public bool RebootRequired { get; init; }
}
