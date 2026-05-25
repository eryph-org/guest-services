using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public class Ec2DataSourceTests
{
    [Fact]
    public async Task ProbeAsync_returns_NotApplicable_when_BIOS_vendor_is_not_Amazon()
    {
        // The test box is not EC2; BIOS vendor will not match "Amazon EC2" and the
        // probe must return NotApplicable (full IMDS impl is gated behind that check).
        var source = new Ec2DataSource(NullLogger<Ec2DataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    [Fact]
    public async Task OnCompletedAsync_is_a_noop()
    {
        var source = new Ec2DataSource(NullLogger<Ec2DataSource>.Instance);

        var act = async () => await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "EC2", InstanceId = "x" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Metadata_properties()
    {
        var source = new Ec2DataSource(NullLogger<Ec2DataSource>.Instance);

        source.Name.Should().Be("EC2");
        source.Priority.Should().Be(20);
        source.RequiresNetwork.Should().BeTrue();
    }
}
