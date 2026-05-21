using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public sealed class ConfigDriveDataSourceTests : IDisposable
{
    private readonly string _root;
    private readonly string _openstackDir;

    public ConfigDriveDataSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-prov-cfgdrv-" + Guid.NewGuid().ToString("N"));
        _openstackDir = Path.Combine(_root, "openstack", "latest");
        Directory.CreateDirectory(_openstackDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_meta_data_json_missing()
    {
        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_reads_all_fields_when_present()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"abc-xyz\",\"hostname\":\"my-host\"}");
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "user_data"),
            "#cloud-config\nhostname: my-host\n");
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "network_data.json"),
            "{\"links\":[]}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("ConfigDrive");
        result.InstanceId.Should().Be("abc-xyz");
        result.Hostname.Should().Be("my-host");
        result.UserData.Should().Contain("hostname: my-host");
        result.NetworkConfig.Should().Be("{\"links\":[]}");
    }

    [Fact]
    public async Task ReadAsync_falls_back_to_name_when_hostname_missing()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"id-1\",\"name\":\"fallback-host\"}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result!.Hostname.Should().Be("fallback-host");
    }

    [Fact]
    public async Task ReadAsync_throws_when_uuid_missing()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"hostname\":\"only-a-name\"}");

        var act = async () => await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsync_throws_descriptive_InvalidDataException_when_meta_data_json_is_malformed()
    {
        var metaDataPath = Path.Combine(_openstackDir, "meta_data.json");
        await File.WriteAllTextAsync(metaDataPath, "{ this is not valid json ");

        var act = async () => await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<InvalidDataException>();
        assertion.Which.Message.Should().Contain("meta_data.json");
        assertion.Which.Message.Should().Contain(metaDataPath);
        assertion.Which.InnerException.Should().BeAssignableTo<System.Text.Json.JsonException>();
    }
}
