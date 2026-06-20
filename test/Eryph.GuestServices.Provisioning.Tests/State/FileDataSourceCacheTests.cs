using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.State;

public sealed class FileDataSourceCacheTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "egs-dscache-" + Guid.NewGuid().ToString("N"));

    private FileDataSourceCache NewCache() =>
        new(NullLogger<FileDataSourceCache>.Instance, _dir);

    [Fact]
    public async Task Load_returns_null_when_absent()
    {
        (await NewCache().LoadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Save_then_load_round_trips_including_non_utf8_bytes()
    {
        // user-data / vendor-data are binary (often gzip, 0x8B is not valid UTF-8).
        // The cache must preserve the bytes verbatim, not round-trip through text.
        var userData = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0xFF, 0x42 };
        var vendorData = new byte[] { 0x80, 0x00, 0x7E, 0xC3 };
        var original = new DataSourceResult
        {
            SourceName = "OpenStack",
            InstanceId = "facade00-0000-4000-8000-000000000001",
            Hostname = "capture-probe",
            DefaultUserName = "osadmin",
            UserData = userData,
            VendorData = vendorData,
            MetaData = new Dictionary<string, string> { ["uuid"] = "facade00", ["az"] = "nova" },
            PlatformMetadata = new PlatformMetadata
            {
                LocalHostname = "capture-probe",
                AvailabilityZone = "nova",
                CloudName = "openstack",
                Platform = "openstack",
                Subplatform = "metadata-service",
                PublicKeys = ["ssh-ed25519 AAAA test"],
            },
            NetworkConfig = "version: 2",
            SshPublicKeys = ["ssh-ed25519 AAAA test"],
        };

        var cache = NewCache();
        await cache.SaveAsync(original, CancellationToken.None);
        var restored = await cache.LoadAsync(CancellationToken.None);

        restored.Should().NotBeNull();
        restored!.InstanceId.Should().Be(original.InstanceId);
        restored.SourceName.Should().Be("OpenStack");
        restored.Hostname.Should().Be("capture-probe");
        restored.DefaultUserName.Should().Be("osadmin");
        restored.UserData.Should().Equal(userData);
        restored.VendorData.Should().Equal(vendorData);
        restored.MetaData.Should().Contain("uuid", "facade00").And.Contain("az", "nova");
        restored.PlatformMetadata!.Subplatform.Should().Be("metadata-service");
        restored.PlatformMetadata.CloudName.Should().Be("openstack");
        restored.SshPublicKeys.Should().Equal("ssh-ed25519 AAAA test");
    }

    [Fact]
    public async Task Load_reparses_v2_network_config_with_match_block()
    {
        // End-to-end regression for issue #59: `run --stage network` does not
        // re-read the cidata disk — it loads datasource.json and re-parses the
        // raw NetworkConfig text. The reporter's config selects the NIC via a
        // v2 `match: {macaddress}` block; that mapping must survive the reload
        // as a populated StructuredNetworkConfig (it previously came back null
        // because the parse threw and was swallowed).
        const string rawNetworkConfig =
            "ethernets:\n" +
            "  eth0:\n" +
            "    addresses: [192.168.8.210/24]\n" +
            "    gateway4: 192.168.8.1\n" +
            "    match: {macaddress: '02:00:00:ad:e2:71'}\n" +
            "    nameservers:\n" +
            "      addresses: [192.168.8.1]\n" +
            "version: 2\n";

        var cache = NewCache();
        await cache.SaveAsync(
            new DataSourceResult
            {
                SourceName = "NoCloud",
                InstanceId = "vm1",
                NetworkConfig = rawNetworkConfig,
            },
            CancellationToken.None);

        var restored = await cache.LoadAsync(CancellationToken.None);

        restored.Should().NotBeNull();
        restored!.StructuredNetworkConfig.Should().NotBeNull();
        restored.StructuredNetworkConfig!.Ethernets.Should().NotBeNull().And.ContainKey("eth0");
        var eth0 = restored.StructuredNetworkConfig.Ethernets!["eth0"];
        eth0.Match!.MacAddress.Should().Be("02:00:00:ad:e2:71");
        eth0.Addresses.Should().BeEquivalentTo(["192.168.8.210/24"]);
        eth0.Gateway4.Should().Be("192.168.8.1");
        eth0.Nameservers!.Addresses.Should().BeEquivalentTo(["192.168.8.1"]);
    }

    [Fact]
    public async Task Reset_removes_the_cache()
    {
        var cache = NewCache();
        await cache.SaveAsync(
            new DataSourceResult { SourceName = "s", InstanceId = "i" }, CancellationToken.None);

        await cache.ResetAsync(CancellationToken.None);

        (await cache.LoadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Load_returns_null_instead_of_throwing_when_file_is_locked()
    {
        // The cache is an optimization; an unreadable file (here: held exclusively)
        // must be treated as absent, not abort provisioning.
        var cache = NewCache();
        await cache.SaveAsync(
            new DataSourceResult { SourceName = "s", InstanceId = "i" }, CancellationToken.None);
        var cachePath = Path.Combine(_dir, "datasource.json");

        using var exclusive = File.Open(cachePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var act = async () => await cache.LoadAsync(CancellationToken.None);
        (await act.Should().NotThrowAsync()).Which.Should().BeNull();
    }

    [Fact]
    public async Task Save_does_not_throw_when_destination_is_locked()
    {
        // A persistent replace failure must not fail the run — SaveAsync is
        // best-effort and swallows it after exhausting retries.
        var cache = NewCache();
        await cache.SaveAsync(
            new DataSourceResult { SourceName = "s", InstanceId = "i" }, CancellationToken.None);
        var cachePath = Path.Combine(_dir, "datasource.json");

        using var exclusive = File.Open(cachePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var act = async () => await cache.SaveAsync(
            new DataSourceResult { SourceName = "s", InstanceId = "i2" }, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
