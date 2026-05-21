using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;

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
        result.UserData.Should().Contain("hostname: my-host");
        result.NetworkConfig.Should().Be("version: 2\n");
        result.MetaData.Should().ContainKey("instance-id");
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
}
