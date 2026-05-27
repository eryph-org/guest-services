using System.Text.Json;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Configuration;

namespace Eryph.GuestServices.Provisioning.Tests.Configuration;

/// <summary>
/// Regression: a partial egs-provisioning.json (e.g. the OpenStack e2e pins
/// only dataSources.dataSourceList) must still yield non-null sibling settings.
/// System.Text.Json source-gen does NOT apply C# property initializers
/// (<c>UserData { get; init; } = new();</c>) for properties absent from the JSON,
/// so a naive Deserialize leaves them null — which made UrlHelper's ctor
/// (<c>s.UserData.FetchMaxAttempts</c>) throw NullReferenceException during
/// container Verify(), crashing egs-service on every boot whenever the file
/// pinned the datasource list.
/// </summary>
public sealed class ProvisioningSettingsDeserializationTests
{
    [Fact]
    public void Raw_deserialize_of_partial_json_leaves_absent_siblings_null()
    {
        // Documents the STJ source-gen behaviour the loader must compensate for:
        // properties absent from the JSON do NOT get their C# initializer value.
        const string json = """{ "dataSources": { "dataSourceList": ["OpenStack"] } }""";

        var settings = JsonSerializer.Deserialize(
            json, SettingsSerializerContext.Default.ProvisioningSettings);

        settings.Should().NotBeNull();
        settings!.DataSources.Should().NotBeNull();
        settings.UserData.Should().BeNull(); // <-- the trap: initializer not applied
    }

    [Fact]
    public void LoadOrDefault_with_partial_file_keeps_sibling_settings_non_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "egs-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "egs-provisioning.json");
        File.WriteAllText(file, """{ "dataSources": { "dataSourceList": ["OpenStack"] } }""");
        try
        {
            var settings = ProvisioningSettings.LoadFromFileOrDefault(file);
            settings.DataSources.DataSourceList.Should().ContainSingle().Which.Should().Be("OpenStack");
            settings.UserData.Should().NotBeNull();
            settings.Scripts.Should().NotBeNull();
            settings.Reboot.Should().NotBeNull();
            settings.DefaultUser.Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
