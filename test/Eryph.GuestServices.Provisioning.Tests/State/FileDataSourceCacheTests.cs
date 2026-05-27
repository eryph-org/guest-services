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
    public async Task Reset_removes_the_cache()
    {
        var cache = NewCache();
        await cache.SaveAsync(
            new DataSourceResult { SourceName = "s", InstanceId = "i" }, CancellationToken.None);

        await cache.ResetAsync(CancellationToken.None);

        (await cache.LoadAsync(CancellationToken.None)).Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
