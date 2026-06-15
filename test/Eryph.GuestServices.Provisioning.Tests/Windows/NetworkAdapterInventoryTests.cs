using System.Text.RegularExpressions;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

public class NetworkAdapterInventoryTests
{
    [Fact]
    public void FormatMac_produces_lowercase_colon_separated()
    {
        var bytes = new byte[] { 0xD2, 0xAB, 0x1C, 0x28, 0x5F, 0xB2 };

        NetworkAdapterInventory.FormatMac(bytes).Should().Be("d2:ab:1c:28:5f:b2");
    }

    [Fact]
    public void Enumerate_returns_adapters_with_a_real_mac_and_index()
    {
        // Regression for the empty-MAC bug: the .NET enumeration must surface a
        // usable hardware MAC (GetPhysicalAddress), not the blank
        // MSFT_NetAdapter.MacAddress. Tolerant of a NIC-less environment: it
        // only asserts the shape of whatever adapters are present.
        var adapters = NetworkAdapterInventory.Enumerate();

        adapters.Should().OnlyContain(a =>
            Regex.IsMatch(a.MacAddress, "^[0-9a-f]{2}(:[0-9a-f]{2}){5}$")
            && a.InterfaceIndex > 0
            && a.IsPhysical
            && !string.IsNullOrEmpty(a.InterfaceAlias));
    }
}
