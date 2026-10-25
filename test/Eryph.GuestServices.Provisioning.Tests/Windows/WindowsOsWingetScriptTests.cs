using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Shape guards for the winget PowerShell that <see cref="WindowsOs"/> emits.
/// The scripts only run on Windows, but the builders are pure string factories
/// so we can assert their structure on any host — this locks in the review
/// verdict (no leftover C# interpolation markers, the Server-2019 gate is
/// present, the wrapper execs winget and propagates its exit code).
/// </summary>
public sealed class WindowsOsWingetScriptTests
{
    [Fact]
    public void ProvisionScript_has_the_platform_gate_and_no_leftover_interpolation()
    {
        var script = PackageScripts.WingetProvision();

        script.Should().NotContain("{{").And.NotContain("}}");
        // Server 2019 (build < 20348) and pre-1809 (build < 17763) are gated out.
        script.Should().Contain("20348").And.Contain("17763");
        script.Should().Contain("Add-AppxProvisionedPackage");
        // The wrapper is written elsewhere now; the provision script must not.
        script.Should().NotContain("winget-system.ps1");
    }

    [Fact]
    public void WrapperScript_execs_winget_and_propagates_exit_code()
    {
        var script = PackageScripts.WingetWrapper();

        script.Should().NotContain("{{").And.NotContain("}}");
        script.Should().Contain("winget.exe");
        script.Should().Contain("exit $LASTEXITCODE");
        // A missing winget.exe must fail loudly instead of falling through to
        // 'exit $LASTEXITCODE' with an unset code (which exits 0).
        script.Should().Contain("Test-Path").And.Contain("exit 1");
        // First meaningful line is not stray-indented (raw-string stripping ok).
        script.Should().NotStartWith(" ").And.NotStartWith("\t");
    }
}
