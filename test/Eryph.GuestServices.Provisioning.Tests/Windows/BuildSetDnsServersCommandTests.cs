using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Direct tests for <see cref="WindowsOs.BuildSetDnsServersCommand"/> — the
/// PowerShell command DNS is applied with. DNS cannot be set through CIM on
/// Windows (MSFT_DNSClientServerAddress has no settable property or static set
/// method, and a raw MI ModifyInstance is rejected as "Invalid parameter"), so
/// the applier shells out to Set-DnsClientServerAddress. A wrong parameter name
/// or a quoting slip here would only surface on a real guest — that exact class
/// of bug (an invalid CIM method name) is what slipped through before.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BuildSetDnsServersCommandTests
{
    [Fact]
    public void Mixed_v4_and_v6_servers_go_into_one_cmdlet_call_quoted()
    {
        var cmd = WindowsOs.BuildSetDnsServersCommand(
            12, ["10.249.249.1", "1.1.1.1", "fd00:dead:beef::1"]);

        cmd.Should().Be(
            "Set-DnsClientServerAddress -InterfaceIndex 12 "
            + "-ServerAddresses '10.249.249.1','1.1.1.1','fd00:dead:beef::1' -ErrorAction Stop");
    }

    [Fact]
    public void Empty_list_resets_the_interface_to_dhcp()
    {
        var cmd = WindowsOs.BuildSetDnsServersCommand(7, []);

        cmd.Should().Be(
            "Set-DnsClientServerAddress -InterfaceIndex 7 -ResetServerAddresses -ErrorAction Stop");
    }

    [Fact]
    public void Null_and_blank_entries_are_dropped()
    {
        // A YAML `~`/null/empty nameserver deserializes to null/blank; it must be
        // dropped (not NRE in FormatPsStringList, not emitted as an empty quote).
        var cmd = WindowsOs.BuildSetDnsServersCommand(3, [null!, "", "  ", "10.0.0.1", " 1.1.1.1 "]);

        cmd.Should().Be(
            "Set-DnsClientServerAddress -InterfaceIndex 3 "
            + "-ServerAddresses '10.0.0.1','1.1.1.1' -ErrorAction Stop");
    }

    [Fact]
    public void List_that_is_all_blank_resets_the_interface()
    {
        // Count > 0 (so the applier's guard lets it through) but every entry is
        // blank — must collapse to a reset, never an invalid -ServerAddresses.
        var cmd = WindowsOs.BuildSetDnsServersCommand(4, [null!, "   "]);

        cmd.Should().Be(
            "Set-DnsClientServerAddress -InterfaceIndex 4 -ResetServerAddresses -ErrorAction Stop");
    }

    [Fact]
    public void Address_with_a_single_quote_is_escaped_and_cannot_break_out()
    {
        // Defensive: addresses come from cloud-config; a stray quote must be
        // doubled so it stays inside the PowerShell single-quoted literal
        // rather than terminating it.
        var cmd = WindowsOs.BuildSetDnsServersCommand(1, ["10.0.0.1'; Remove-Item C:\\ #"]);

        cmd.Should().Contain("'10.0.0.1''; Remove-Item C:\\ #'");
        cmd.Should().StartWith("Set-DnsClientServerAddress -InterfaceIndex 1 -ServerAddresses '");
        cmd.Should().EndWith("-ErrorAction Stop");
    }
}
