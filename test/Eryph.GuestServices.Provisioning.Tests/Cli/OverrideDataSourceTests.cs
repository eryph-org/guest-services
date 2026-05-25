using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

public sealed class OverrideDataSourceTests
{
    [Fact]
    public async Task ProbeAsync_returns_Ready_with_supplied_instance_id_and_user_data()
    {
        var bytes = Encoding.UTF8.GetBytes("#cloud-config\nhostname: x\n");
        var sut = new OverrideDataSource("my-instance", bytes);

        var probe = await sut.ProbeAsync(CancellationToken.None);

        var ready = probe.Should().BeOfType<DataSourceProbeResult.Ready>().Subject;
        ready.Data.InstanceId.Should().Be("my-instance");
        ready.Data.UserData.Should().Equal(bytes);
        ready.Data.SourceName.Should().Be("override");
    }

    // Regression: real-world user-data is often gzipped binary (eryph-zero ships
    // gzipped multipart MIME). The override datasource must carry the raw bytes
    // without any UTF-8 round-trip — see DataSourceResult.UserData notes.
    [Fact]
    public async Task ProbeAsync_RoundTripsBinaryGzipBytes()
    {
        // Gzip magic 1F 8B 08 plus arbitrary follow-up bytes that include 0x8B —
        // an invalid UTF-8 leading byte. If the override layer ever passed this
        // through ReadAllText (which it did, until this fix), 0x8B would become
        // EF BF BD (replacement char), corrupting the gzip header.
        var raw = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0xFF, 0xAA, 0x8B, 0x00 };
        var sut = new OverrideDataSource("inst", raw);
        var probe = await sut.ProbeAsync(CancellationToken.None);
        var ready = (DataSourceProbeResult.Ready)probe;

        ready.Data.UserData.Should().Equal(raw);
    }

    [Fact]
    public void Priority_is_lower_than_real_datasources()
    {
        // Real datasources start at Azure=10. The override must win discovery.
        var sut = new OverrideDataSource("i-1", null);

        sut.Priority.Should().BeLessThan(10);
    }
}
