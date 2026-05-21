using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

public sealed class OverrideDataSourceTests
{
    [Fact]
    public async Task ProbeAsync_returns_Ready_with_supplied_instance_id_and_user_data()
    {
        var sut = new OverrideDataSource("my-instance", "#cloud-config\nhostname: x\n");

        var probe = await sut.ProbeAsync(CancellationToken.None);

        var ready = probe.Should().BeOfType<DataSourceProbeResult.Ready>().Subject;
        ready.Data.InstanceId.Should().Be("my-instance");
        ready.Data.UserData.Should().Be("#cloud-config\nhostname: x\n");
        ready.Data.SourceName.Should().Be("override");
    }

    [Fact]
    public void Priority_is_lower_than_real_datasources()
    {
        // Real datasources start at Azure=10. The override must win discovery.
        var sut = new OverrideDataSource("i-1", null);

        sut.Priority.Should().BeLessThan(10);
    }
}
