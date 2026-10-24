using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Shape guard for the Chocolatey bootstrap PowerShell that <see cref="WindowsOs"/>
/// emits. The script only runs on Windows, but the builder is a pure string
/// factory so its structure can be asserted on any host.
/// </summary>
public sealed class WindowsOsChocolateyScriptTests
{
    [Fact]
    public void BootstrapScript_downloads_installer_and_signals_reboot()
    {
        var script = PackageScripts.ChocolateyBootstrap();

        script.Should().Contain("community.chocolatey.org/install.ps1");
        // All streams merged so the installer's Write-Warning/Write-Host reboot
        // notice is captured (2>&1 would miss it — the bug the review caught).
        script.Should().Contain("*>&1");
        // The reboot path exits with the code the OS layer maps to RebootRequired.
        script.Should().Contain("reboot is required").And.Contain("exit 3010");
    }
}
