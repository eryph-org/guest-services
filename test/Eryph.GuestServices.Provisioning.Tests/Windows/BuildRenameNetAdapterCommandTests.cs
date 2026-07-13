using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Direct tests for <see cref="WindowsOs.BuildRenameNetAdapterCommand"/> — the
/// PowerShell command a MAC-matched adapter is renamed with (cloud-init v1
/// <c>name</c> / v2 <c>set-name</c>). The adapter is keyed by its stable
/// interface index and piped into Rename-NetAdapter so the current connection
/// name never has to be quoted; a wrong cmdlet/parameter name or a quoting slip
/// here would only surface on a real guest.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BuildRenameNetAdapterCommandTests
{
    [Fact]
    public void Renames_adapter_by_interface_index()
    {
        var cmd = WindowsOs.BuildRenameNetAdapterCommand(12, "eth0");

        cmd.Should().Be(
            "Get-NetAdapter -InterfaceIndex 12 | Rename-NetAdapter -NewName 'eth0' -ErrorAction Stop");
    }

    [Fact]
    public void New_name_with_a_single_quote_is_escaped_and_cannot_break_out()
    {
        // Defensive: the name comes from cloud-config; a stray quote must be
        // doubled so it stays inside the PowerShell single-quoted literal.
        var cmd = WindowsOs.BuildRenameNetAdapterCommand(1, "eth0'; Remove-Item C:\\ #");

        cmd.Should().Contain("-NewName 'eth0''; Remove-Item C:\\ #'");
        cmd.Should().StartWith("Get-NetAdapter -InterfaceIndex 1 | Rename-NetAdapter -NewName '");
        cmd.Should().EndWith("-ErrorAction Stop");
    }
}
