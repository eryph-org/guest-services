using AwesomeAssertions;

namespace Eryph.GuestServices.CloudConfig.Tests;

public class PlatformMetadataTests
{
    [Fact]
    public void Record_with_all_fields_round_trips_via_with_expression()
    {
        var original = new PlatformMetadata
        {
            LocalHostname = "my-host",
            PublicKeys = ["ssh-rsa AAAA key-1", "ssh-ed25519 BBBB key-2"],
            AvailabilityZone = "westeurope-1",
            Region = "westeurope",
            CloudName = "azure",
            Platform = "azure",
            Subplatform = "metadata",
            InstanceType = "Standard_D2s_v5",
        };

        var copy = original with { };

        copy.Should().Be(original);
        copy.LocalHostname.Should().Be("my-host");
        copy.PublicKeys.Should().BeEquivalentTo(["ssh-rsa AAAA key-1", "ssh-ed25519 BBBB key-2"]);
        copy.AvailabilityZone.Should().Be("westeurope-1");
        copy.Region.Should().Be("westeurope");
        copy.CloudName.Should().Be("azure");
        copy.Platform.Should().Be("azure");
        copy.Subplatform.Should().Be("metadata");
        copy.InstanceType.Should().Be("Standard_D2s_v5");
    }

    [Fact]
    public void Empty_record_has_all_nullable_fields_unset()
    {
        var empty = new PlatformMetadata();

        empty.LocalHostname.Should().BeNull();
        empty.PublicKeys.Should().BeNull();
        empty.AvailabilityZone.Should().BeNull();
        empty.Region.Should().BeNull();
        empty.CloudName.Should().BeNull();
        empty.Platform.Should().BeNull();
        empty.Subplatform.Should().BeNull();
        empty.InstanceType.Should().BeNull();
    }
}
