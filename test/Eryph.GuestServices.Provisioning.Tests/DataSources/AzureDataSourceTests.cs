using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public class AzureDataSourceTests
{
    [Fact]
    public async Task ProbeAsync_returns_NotApplicable_when_no_Azure_VmId()
    {
        // On a CI host that is not Azure (no VmId registry key), the probe must
        // return NotApplicable regardless of CustomData.bin existing.
        if (OperatingSystem.IsWindows() && AzureVmIdRegistryExists())
        {
            return; // Running on a real Azure-flavoured Windows host — skip.
        }

        var source = new AzureDataSource(NullLogger<AzureDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    [Fact]
    public async Task OnCompletedAsync_is_a_noop()
    {
        var source = new AzureDataSource(NullLogger<AzureDataSource>.Instance);

        var act = async () => await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "Azure", InstanceId = "x" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Metadata_properties()
    {
        var source = new AzureDataSource(NullLogger<AzureDataSource>.Instance);

        source.Name.Should().Be("Azure");
        source.Priority.Should().Be(10);
        source.RequiresNetwork.Should().BeFalse();
    }

    [SupportedOSPlatform("windows")]
    private static bool AzureVmIdRegistryExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AzureDataSource.AzureVmIdKey);
            return key?.GetValue(AzureDataSource.AzureVmIdValue) is string s && !string.IsNullOrEmpty(s);
        }
        catch
        {
            return false;
        }
    }
}
