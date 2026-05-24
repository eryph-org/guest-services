using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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

    // A platform probe that reports we are NOT on Azure. Injected so the probe
    // result is independent of the host platform — an Azure-hosted CI agent
    // would otherwise flip the defensive Azure opt-out and short-circuit to
    // NotApplicable, masking the config-2 volume we just staged.
    private static IPlatformProbe NotOnAzure()
    {
        var platformProbe = Substitute.For<IPlatformProbe>();
        platformProbe.IsRunningOnAzure().Returns(false);
        return platformProbe;
    }

    // Writes meta_data.json under openstack/<version>/, creating the directory.
    private async Task WriteMetaDataAsync(string version, string json)
    {
        var dir = Path.Combine(_root, "openstack", version);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "meta_data.json"), json);
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
            "{\"uuid\":\"abc-xyz\",\"hostname\":\"my-host\",\"availability_zone\":\"az1\"}");
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "user_data"),
            "#cloud-config\nhostname: my-host\n");
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "network_data.json"),
            "{\"links\":[]}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("ConfigDrive");
        result.InstanceId.Should().Be("abc-xyz");
        result.Hostname.Should().Be("my-host");
        System.Text.Encoding.UTF8.GetString(result.UserData!).Should().Contain("hostname: my-host");
        result.NetworkConfig.Should().Be("{\"links\":[]}");

        result.PlatformMetadata.Should().NotBeNull();
        result.PlatformMetadata!.LocalHostname.Should().Be("my-host");
        result.PlatformMetadata!.AvailabilityZone.Should().Be("az1");
        result.PlatformMetadata!.CloudName.Should().Be("openstack");
        result.PlatformMetadata!.Subplatform.Should().Be("config-drive");
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

    [Fact]
    public async Task ReadAsync_carries_vendor_data_when_present()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"u\"}");
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "vendor_data.json"),
            "{\"some\":\"thing\"}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        System.Text.Encoding.UTF8.GetString(result!.VendorData!).Should().Be("{\"some\":\"thing\"}");
    }

    [Fact]
    public async Task ProbeAsync_returns_NotApplicable_when_no_volume()
    {
        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([]);

        var source = new ConfigDriveDataSource(probe, NotOnAzure(), NullLogger<ConfigDriveDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    [Fact]
    public async Task ProbeAsync_returns_Ready_when_volume_present_and_valid()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"abc\"}");

        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([new MountedVolume("config-2", _root)]);

        var source = new ConfigDriveDataSource(probe, NotOnAzure(), NullLogger<ConfigDriveDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Ready>();
        ((DataSourceProbeResult.Ready)result).Data.InstanceId.Should().Be("abc");
    }

    [Fact]
    public async Task ProbeAsync_returns_Failed_when_meta_data_json_malformed()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{ not json ");

        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([new MountedVolume("config-2", _root)]);

        var source = new ConfigDriveDataSource(probe, NotOnAzure(), NullLogger<ConfigDriveDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Failed>();
    }

    [Fact]
    public async Task OnCompletedAsync_is_a_noop_and_does_not_touch_filesystem()
    {
        // RFC 0005: ConfigDrive cleanup is a no-op by design — eryph-zero keeps
        // the config-2 ISO attached so `egs-tool reset` can re-read the same
        // payload. Verify nothing on the temp tree is removed.
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"abc\"}");

        var probe = Substitute.For<IVolumeProbe>();
        var source = new ConfigDriveDataSource(probe, NotOnAzure(), NullLogger<ConfigDriveDataSource>.Instance);

        await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "ConfigDrive", InstanceId = "abc" },
            CancellationToken.None);

        // Calling it twice must also be safe — idempotency.
        await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "ConfigDrive", InstanceId = "abc" },
            CancellationToken.None);

        File.Exists(Path.Combine(_openstackDir, "meta_data.json")).Should().BeTrue();
    }

    // ---- version walk (cloud-init _find_working_version) ----

    [Fact]
    public async Task ReadAsync_reads_a_dated_version_directory_when_latest_absent()
    {
        await WriteMetaDataAsync("2018-08-27", "{\"uuid\":\"dated-id\"}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result.Should().NotBeNull();
        result!.InstanceId.Should().Be("dated-id");
    }

    [Fact]
    public async Task ReadAsync_picks_the_newest_present_version()
    {
        // Both 2017-02-22 and 2018-08-27 present -> newest (2018-08-27) wins.
        await WriteMetaDataAsync("2017-02-22", "{\"uuid\":\"older\"}");
        await WriteMetaDataAsync("2018-08-27", "{\"uuid\":\"newer\"}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("newer");
    }

    [Fact]
    public async Task ReadAsync_falls_back_to_latest_when_no_dated_version_present()
    {
        // Regression: the prior hardcoded behavior. Only openstack/latest present.
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"latest-id\"}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("latest-id");
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_only_an_unknown_version_present_and_no_latest()
    {
        // An unknown version dir that is NOT in OS_VERSIONS, and no `latest`.
        await WriteMetaDataAsync("2099-01-01", "{\"uuid\":\"unknown\"}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result.Should().BeNull();
    }

    // ---- public_keys (cloud-init normalize_pubkey_data) ----

    [Fact]
    public async Task ReadAsync_surfaces_public_keys_dict_single_entry()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"u\",\"public_keys\":{\"mykey\":\"ssh-ed25519 AAA... user@host\"}}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result!.SshPublicKeys.Should().BeEquivalentTo(["ssh-ed25519 AAA... user@host"]);
    }

    [Fact]
    public async Task ReadAsync_surfaces_public_keys_dict_multiple_entries()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"u\",\"public_keys\":{\"a\":\"ssh-ed25519 KEY-A a@h\",\"b\":\"ssh-rsa KEY-B b@h\"}}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result!.SshPublicKeys.Should().BeEquivalentTo(
            ["ssh-ed25519 KEY-A a@h", "ssh-rsa KEY-B b@h"]);
    }

    [Fact]
    public async Task ReadAsync_tolerates_public_keys_array_form()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"u\",\"public_keys\":[\"ssh-ed25519 ARR-1 a@h\",\"ssh-rsa ARR-2 b@h\"]}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result!.SshPublicKeys.Should().BeEquivalentTo(
            ["ssh-ed25519 ARR-1 a@h", "ssh-rsa ARR-2 b@h"]);
    }

    [Fact]
    public async Task ReadAsync_tolerates_public_keys_string_form_splitlines()
    {
        // A single string -> splitlines() (cloud-init normalize_pubkey_data).
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"u\",\"public_keys\":\"ssh-ed25519 LINE-1 a@h\\nssh-rsa LINE-2 b@h\"}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result!.SshPublicKeys.Should().BeEquivalentTo(
            ["ssh-ed25519 LINE-1 a@h", "ssh-rsa LINE-2 b@h"]);
    }

    [Fact]
    public async Task ReadAsync_leaves_public_keys_null_when_absent()
    {
        await File.WriteAllTextAsync(Path.Combine(_openstackDir, "meta_data.json"),
            "{\"uuid\":\"u\"}");

        var result = await ConfigDriveDataSource.ReadAsync(_root, CancellationToken.None);

        result!.SshPublicKeys.Should().BeNull();
    }

    // ---- real-world fixture (test/fixtures/configdrive/) ----

    [Fact]
    public async Task ReadAsync_reads_real_world_configdrive_fixture()
    {
        // The fixture lives under a dated version dir (no `latest`), so this
        // also exercises the version-walk (Finding 19) and public_keys surfacing
        // (Finding 20). See test/fixtures/configdrive/README.md.
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "configdrive");
        Directory.Exists(Path.Combine(fixtureRoot, "openstack", "2018-08-27"))
            .Should().BeTrue("the ConfigDrive fixture should be copied to the output");

        var result = await ConfigDriveDataSource.ReadAsync(fixtureRoot, CancellationToken.None);

        result.Should().NotBeNull();
        result!.InstanceId.Should().Be("d8e02d56-2648-49a3-bf97-6be8f1204f38");
        result.Hostname.Should().Be("openstack-host.example.org");
        result.SshPublicKeys.Should().NotBeNull();
        result.SshPublicKeys.Should().Contain(k => k.StartsWith("ssh-ed25519 ") && k.EndsWith("admin@example.org"));
        result.SshPublicKeys.Should().HaveCount(2);
    }
}
