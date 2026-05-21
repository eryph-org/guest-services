using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public sealed class NoCloudDataSourceTests : IDisposable
{
    private readonly string _root;

    public NoCloudDataSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-prov-nocloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_meta_data_missing()
    {
        var result = await NoCloudDataSource.ReadAsync(_root, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_reads_all_fields_when_present()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: i-abc-123\nlocal-hostname: my-host\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "user-data"),
            "#cloud-config\nhostname: my-host\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "network-config"),
            "version: 2\n");

        var result = await NoCloudDataSource.ReadAsync(_root, CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("NoCloud");
        result.InstanceId.Should().Be("i-abc-123");
        result.Hostname.Should().Be("my-host");
        System.Text.Encoding.UTF8.GetString(result.UserData!).Should().Contain("hostname: my-host");
        result.NetworkConfig.Should().Be("version: 2\n");
        result.MetaData.Should().ContainKey("instance-id");

        result.PlatformMetadata.Should().NotBeNull();
        result.PlatformMetadata!.LocalHostname.Should().Be("my-host");
        result.PlatformMetadata!.CloudName.Should().Be("nocloud");

        result.StructuredNetworkConfig.Should().NotBeNull();
        result.StructuredNetworkConfig!.Version.Should().Be(2);
    }

    [Fact]
    public async Task ReadAsync_throws_when_instance_id_missing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "local-hostname: just-a-host\n");

        var act = async () => await NoCloudDataSource.ReadAsync(_root, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsync_strips_quotes_from_values()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: \"i-quoted\"\nlocal-hostname: 'host'\n");

        var result = await NoCloudDataSource.ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("i-quoted");
        result.Hostname.Should().Be("host");
    }

    // Regression: NoCloud previously read user-data via ReadAllTextAsync, which
    // corrupts gzipped binary content (UTF-8 invalid byte 0x8B becomes EF BF BD).
    // eryph-zero's configdrive ships gzipped multipart MIME for user-data, so a
    // text round-trip destroyed the gzip header and the pipeline silently
    // dropped every cloud-config payload.
    [Fact]
    public async Task ReadAsync_PreservesGzippedUserDataBytesExactly()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"), "instance-id: i\n");
        // Gzip of "hello"
        var gzipped = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
                                   0xCB, 0x48, 0xCD, 0xC9, 0xC9, 0x07, 0x00,
                                   0x86, 0xA6, 0x10, 0x36, 0x05, 0x00, 0x00, 0x00 };
        await File.WriteAllBytesAsync(Path.Combine(_root, "user-data"), gzipped);

        var result = await NoCloudDataSource.ReadAsync(_root, CancellationToken.None);

        result!.UserData.Should().Equal(gzipped);
    }

    [Fact]
    public async Task ReadAsync_carries_vendor_data_through_when_present()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"), "instance-id: i\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "vendor-data"), "vendor payload");

        var result = await NoCloudDataSource.ReadAsync(_root, CancellationToken.None);

        System.Text.Encoding.UTF8.GetString(result!.VendorData!).Should().Be("vendor payload");
        result.GetVendorDataBytes().Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProbeAsync_returns_NotApplicable_when_no_volume_matches()
    {
        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([]);

        var source = new NoCloudDataSource(probe, NullLogger<NoCloudDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    [Fact]
    public async Task ProbeAsync_returns_Ready_when_volume_present_and_valid()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"), "instance-id: i-1\n");

        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([new MountedVolume("cidata", _root)]);

        var source = new NoCloudDataSource(probe, NullLogger<NoCloudDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Ready>();
        ((DataSourceProbeResult.Ready)result).Data.InstanceId.Should().Be("i-1");
    }

    [Fact]
    public async Task ProbeAsync_returns_Failed_when_meta_data_is_malformed()
    {
        // No instance-id present — ReadAsync throws InvalidDataException, which the
        // probe must surface as Failed (not bubble out).
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"), "local-hostname: x\n");

        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([new MountedVolume("cidata", _root)]);

        var source = new NoCloudDataSource(probe, NullLogger<NoCloudDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Failed>();
    }

    [Fact]
    public async Task OnCompletedAsync_is_a_noop()
    {
        var probe = Substitute.For<IVolumeProbe>();
        var source = new NoCloudDataSource(probe, NullLogger<NoCloudDataSource>.Instance);

        var act = async () => await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "NoCloud", InstanceId = "x" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
