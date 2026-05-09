using System.Text.Json;
using AwesomeAssertions;
using Eryph.GuestServices.Tool.Commands;

namespace Eryph.GuestServices.Tool.Tests;

public class GetDataCommandTests
{
    [Fact]
    public void BuildJson_EmitsTheExpectedTopLevelKeys()
    {
        var json = GetDataCommand.BuildJson(
            guest: new Dictionary<string, string>(),
            guestIntrinsic: new Dictionary<string, string>(),
            external: new Dictionary<string, string>(),
            hostOnly: new Dictionary<string, string>());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.EnumerateObject().Select(p => p.Name).Should()
            .Equal("guest", "guest_intrinsic", "external", "host_only");
    }

    [Fact]
    public void BuildJson_EmbedsParseableJsonValuesAsObjectsNotEscapedStrings()
    {
        // cloud-init writes JSON blobs into KVP values; the command parses them
        // so the final document is a single, navigable JSON tree rather than
        // a tree with escaped JSON strings inside.
        var guest = new Dictionary<string, string>
        {
            ["cloud_init"] = """{ "instance_id": "i-123", "platform": "hyperv" }""",
        };

        var json = GetDataCommand.BuildJson(
            guest,
            guestIntrinsic: new Dictionary<string, string>(),
            external: new Dictionary<string, string>(),
            hostOnly: new Dictionary<string, string>());

        using var doc = JsonDocument.Parse(json);
        var value = doc.RootElement.GetProperty("guest").GetProperty("cloud_init");

        value.ValueKind.Should().Be(JsonValueKind.Object);
        value.GetProperty("instance_id").GetString().Should().Be("i-123");
        value.GetProperty("platform").GetString().Should().Be("hyperv");
    }

    [Fact]
    public void BuildJson_EmitsPlainStringValuesAsJsonStrings_AndIndentsTheOutput()
    {
        var guest = new Dictionary<string, string>
        {
            ["status"] = "available",
        };
        // Values that look like JSON but fail to parse must fall back to a
        // plain JSON string so callers do not lose data.
        var external = new Dictionary<string, string>
        {
            ["broken"] = "{ not really json",
        };

        var json = GetDataCommand.BuildJson(
            guest,
            guestIntrinsic: new Dictionary<string, string>(),
            external,
            hostOnly: new Dictionary<string, string>());

        json.Should().Contain("\n", "WriteIndented should produce a multi-line document");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("guest").GetProperty("status")
            .GetString().Should().Be("available");
        doc.RootElement.GetProperty("external").GetProperty("broken")
            .GetString().Should().Be("{ not really json");
    }
}
