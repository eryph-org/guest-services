using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

public class NetworkAdapterInventoryTests
{
    [Theory]
    [InlineData(new byte[] { 0xD2, 0xAB, 0x1C, 0x28, 0x5F, 0xB2 }, "d2:ab:1c:28:5f:b2")]
    [InlineData(new byte[] { 0x00, 0x0E, 0x1C, 0x00, 0x5F, 0x00 }, "00:0e:1c:00:5f:00")] // leading-zero nibbles
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "ff:ff:ff:ff:ff:ff")]
    public void FormatMac_produces_lowercase_colon_separated_with_leading_zeros(byte[] bytes, string expected)
    {
        NetworkAdapterInventory.FormatMac(bytes).Should().Be(expected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Enumerate_returns_adapters_with_a_real_mac_and_index()
    {
        // Regression for the empty-MAC bug: the .NET enumeration must surface a
        // usable hardware MAC (GetPhysicalAddress), not the blank
        // MSFT_NetAdapter.MacAddress. Asserts the build host has at least one
        // MAC-bearing NIC and every entry is well-formed (non-vacuous).
        var adapters = NetworkAdapterInventory.Enumerate();

        adapters.Should().NotBeEmpty();
        adapters.Should().OnlyContain(a =>
            Regex.IsMatch(a.MacAddress, "^[0-9a-f]{2}(:[0-9a-f]{2}){5}$")
            && a.InterfaceIndex > 0
            && !string.IsNullOrEmpty(a.InterfaceAlias));
    }
}
