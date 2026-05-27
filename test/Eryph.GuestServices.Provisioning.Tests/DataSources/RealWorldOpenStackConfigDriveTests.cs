using System.Text.Json;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

/// <summary>
/// Drives <see cref="ConfigDriveDataSource"/> against a real config-2 tree
/// emitted by nova (DevStack), hand-stripped of secrets. This is the
/// "process real OpenStack data" end-to-end check for the ConfigDrive
/// datasource: it pins the reader against the exact field set, nesting and
/// types a real OpenStack producer writes, not synthetic JSON.
///
/// See test/fixtures/configdrive-openstack/README.md for provenance and the
/// sanitized values asserted below. Assertions are structural or against the
/// sanitized placeholders so a failure never echoes captured payloads.
/// </summary>
public sealed class RealWorldOpenStackConfigDriveTests
{
    private const string SanitizedUuid = "facade00-0000-4000-8000-000000000001";
    private const string SanitizedHostname = "egs-fixture.novalocal";
    private const string SanitizedPublicKey =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5FIXTUREKEY egs@fixture";

    private static string FixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "configdrive-openstack");

    [Fact]
    public void Fixture_is_copied_to_the_output()
    {
        Directory.Exists(Path.Combine(FixtureRoot, "openstack", "2018-08-27"))
            .Should().BeTrue(
                "the real-world ConfigDrive fixture must be copied to the test output " +
                "(check the csproj CopyToOutputDirectory wiring)");
    }

    [Fact]
    public async Task ReadAsync_reads_the_real_nova_configdrive()
    {
        var result = await ConfigDriveDataSource.ReadAsync(FixtureRoot, CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("ConfigDrive");
        result.InstanceId.Should().Be(SanitizedUuid);
        result.Hostname.Should().Be(SanitizedHostname);

        result.PlatformMetadata.Should().NotBeNull();
        result.PlatformMetadata!.CloudName.Should().Be("openstack");
        result.PlatformMetadata.Platform.Should().Be("openstack");
        result.PlatformMetadata.Subplatform.Should().Be("config-drive");
        result.PlatformMetadata.AvailabilityZone.Should().Be("nova");
        result.PlatformMetadata.LocalHostname.Should().Be(SanitizedHostname);
    }

    [Fact]
    public async Task ReadAsync_surfaces_the_nested_public_keys_from_real_metadata()
    {
        var result = await ConfigDriveDataSource.ReadAsync(FixtureRoot, CancellationToken.None);

        // Real nova writes public_keys as a name->key object; normalize_pubkey_data
        // discards the names and yields the key material. Every value was
        // sanitized to the same placeholder.
        result!.SshPublicKeys.Should().NotBeNullOrEmpty();
        result.SshPublicKeys.Should().AllBe(SanitizedPublicKey);
    }

    [Fact]
    public async Task ReadAsync_carries_the_real_user_data_as_bytes()
    {
        var result = await ConfigDriveDataSource.ReadAsync(FixtureRoot, CancellationToken.None);

        // nova writes the instance user-data verbatim to openstack/<v>/user_data;
        // the datasource carries it through as raw bytes for the pipeline.
        result!.UserData.Should().NotBeNull();
        var userData = System.Text.Encoding.UTF8.GetString(result.UserData!);
        userData.Should().StartWith("#cloud-config");
    }

    [Fact]
    public async Task ReadAsync_carries_real_network_data_json_as_parseable_json()
    {
        var result = await ConfigDriveDataSource.ReadAsync(FixtureRoot, CancellationToken.None);

        // nova emits network_data.json (OpenStack 3-section schema). The
        // datasource carries it through as raw text; assert it is present and
        // valid JSON with the documented `links` array — without echoing it.
        result!.NetworkConfig.Should().NotBeNullOrWhiteSpace();
        IsJsonObjectWith(result.NetworkConfig!, "links").Should().BeTrue(
            "real OpenStack network_data.json is a JSON object containing a `links` array");
    }

    [Fact]
    public async Task ReadAsync_converts_empty_vendor_data_json_to_no_payload()
    {
        var result = await ConfigDriveDataSource.ReadAsync(FixtureRoot, CancellationToken.None);

        // Real nova emits vendor_data.json = {} (no "cloud-init" key).
        // convert_vendordata maps that to no runnable vendor cloud-config, so
        // the datasource surfaces VendorData as null rather than the raw JSON.
        result!.VendorData.Should().BeNull();
    }

    private static bool IsJsonObjectWith(string text, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
