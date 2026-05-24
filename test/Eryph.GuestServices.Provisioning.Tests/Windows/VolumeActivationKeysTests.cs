using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows.Licensing;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

[SupportedOSPlatform("windows")]
public sealed class VolumeActivationKeysTests
{
    // Pinning a handful of the table entries so refactors of the lookup
    // can't silently change which key gets returned. The values come from
    // Microsoft Learn's AVMA and KMS reference pages — they are PUBLIC and
    // documented for the explicit purpose of activating Windows guests.
    // Verified against the Microsoft Learn AVMA and KMS reference pages.
    // Pinning these keeps refactors of the lookup honest.
    [Theory]
    // AVMA keys (Server 2012R2 through 2025)
    [InlineData(OsVersionFamily.WindowsServer2012R2, "ServerDatacenter", VolumeActivationType.Avma, "Y4TGP-NPTV9-HTC2H-7MGQ3-DV4TW")]
    [InlineData(OsVersionFamily.WindowsServer2016, "ServerStandard", VolumeActivationType.Avma, "C3RCX-M6NRP-6CXC9-TW2F2-4RHYD")]
    [InlineData(OsVersionFamily.WindowsServer2019, "ServerDatacenter", VolumeActivationType.Avma, "H3RNG-8C32Q-Q8FRX-6TDXV-WMBMW")]
    [InlineData(OsVersionFamily.WindowsServer2019, "ServerSolution", VolumeActivationType.Avma, "2CTP7-NHT64-BP62M-FV6GG-HFV28")]
    [InlineData(OsVersionFamily.WindowsServer2022, "ServerDatacenter", VolumeActivationType.Avma, "W3GNR-8DDXR-2TFRP-H8P33-DV9BG")]
    [InlineData(OsVersionFamily.WindowsServer2022, "ServerStandard", VolumeActivationType.Avma, "YDFWN-MJ9JR-3DYRK-FXXRW-78VHK")]
    [InlineData(OsVersionFamily.WindowsServer2025, "ServerDatacenter", VolumeActivationType.Avma, "YQB4H-NKHHJ-Q6K4R-4VMY6-VCH67")]
    [InlineData(OsVersionFamily.WindowsServer2025, "ServerStandard", VolumeActivationType.Avma, "WWVGQ-PNHV9-B89P4-8GGM9-9HPQ4")]
    // KMS keys
    [InlineData(OsVersionFamily.WindowsServer2019, "ServerDatacenter", VolumeActivationType.Kms, "WMDGN-G9PQG-XVVXX-R3X43-63DFG")]
    [InlineData(OsVersionFamily.WindowsServer2022, "ServerStandard", VolumeActivationType.Kms, "VDYBN-27WPP-V4HQT-9VMD4-VMK7H")]
    [InlineData(OsVersionFamily.WindowsServer2025, "ServerDatacenter", VolumeActivationType.Kms, "D764K-2NDRG-47T6Q-P8T8W-YP6DF")]
    [InlineData(OsVersionFamily.WindowsServer2025, "ServerStandard", VolumeActivationType.Kms, "TVRH6-WHNXV-R9WG3-9XRFY-MY832")]
    public void Lookup_returns_known_key(
        OsVersionFamily osFamily, string licenseFamily, VolumeActivationType type, string expected)
    {
        VolumeActivationKeys.Lookup(osFamily, licenseFamily, type).Should().Be(expected);
    }

    [Theory]
    // Datacenter: Azure Edition has a separate KMS / AVMA key per release.
    // The LicenseFamily Windows reports for these guests is one of several
    // observed names — accept all of them.
    [InlineData("ServerAzureEditionDatacenter")]
    [InlineData("ServerDatacenterAzureEdition")]
    [InlineData("ServerAzureCor")]
    public void Server2022_DatacenterAzureEdition_AVMA_is_resolved(string licenseFamily)
    {
        VolumeActivationKeys.Lookup(OsVersionFamily.WindowsServer2022, licenseFamily, VolumeActivationType.Avma)
            .Should().Be("F7TB6-YKN8Y-FCC6R-KQ484-VMK3J");
    }

    [Fact]
    public void Server2025_DatacenterAzureEdition_AVMA_is_resolved()
    {
        VolumeActivationKeys.Lookup(
            OsVersionFamily.WindowsServer2025, "ServerAzureEditionDatacenter", VolumeActivationType.Avma)
            .Should().Be("6NMQ9-T38WF-6MFGM-QYGYM-88J4F");
    }

    [Fact]
    public void ServerDatacenterCore_is_treated_as_ServerDatacenter()
    {
        // The license family for Core editions differs by suffix — same key
        // applies. We pin both lookups so a refactor can't drop the Core alias.
        var dc = VolumeActivationKeys.Lookup(
            OsVersionFamily.WindowsServer2022, "ServerDatacenter", VolumeActivationType.Avma);
        var dcCore = VolumeActivationKeys.Lookup(
            OsVersionFamily.WindowsServer2022, "ServerDatacenterCore", VolumeActivationType.Avma);
        dc.Should().NotBeNull();
        dcCore.Should().Be(dc);
    }

    [Fact]
    public void Unknown_OS_returns_null()
    {
        VolumeActivationKeys.Lookup(OsVersionFamily.Unknown, "ServerDatacenter", VolumeActivationType.Avma)
            .Should().BeNull();
    }

    [Fact]
    public void Unknown_license_family_returns_null()
    {
        VolumeActivationKeys.Lookup(OsVersionFamily.WindowsServer2022, "Klingon", VolumeActivationType.Avma)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(6, 2, 9200, OsVersionFamily.WindowsServer2012)]
    [InlineData(6, 3, 9600, OsVersionFamily.WindowsServer2012R2)]
    [InlineData(10, 0, 14393, OsVersionFamily.WindowsServer2016)]
    [InlineData(10, 0, 17763, OsVersionFamily.WindowsServer2019)]
    [InlineData(10, 0, 20348, OsVersionFamily.WindowsServer2022)]
    [InlineData(10, 0, 26100, OsVersionFamily.WindowsServer2025)]
    public void OsVersionDetector_buckets_known_build_numbers(
        int major, int minor, int build, OsVersionFamily expected)
    {
        // The build-number boundaries are the official Microsoft release
        // numbers — these must stay pinned or a regression silently picks
        // the wrong AVMA / KMS key table for newer guests.
        OsVersionDetector.Detect(new Version(major, minor, build)).Should().Be(expected);
    }

    [Fact]
    public void OsVersionDetector_unknown_major_returns_Unknown()
    {
        OsVersionDetector.Detect(new Version(5, 1, 2600)).Should().Be(OsVersionFamily.Unknown);
    }
}
