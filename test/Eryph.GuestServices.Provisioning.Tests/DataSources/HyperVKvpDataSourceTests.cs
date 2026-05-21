using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public class HyperVKvpDataSourceTests
{
    [Fact]
    public async Task ProbeAsync_returns_NotApplicable_when_no_VirtualMachineId()
    {
        if (OperatingSystem.IsWindows() && HyperVRegistryExists())
        {
            return; // Test box is itself a Hyper-V guest — skip.
        }

        var source = new HyperVKvpDataSource(NullLogger<HyperVKvpDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    [Fact]
    public async Task OnCompletedAsync_is_a_noop()
    {
        var source = new HyperVKvpDataSource(NullLogger<HyperVKvpDataSource>.Instance);

        var act = async () => await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "Hyper-V KVP", InstanceId = "x" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Metadata_properties()
    {
        var source = new HyperVKvpDataSource(NullLogger<HyperVKvpDataSource>.Instance);

        source.Name.Should().Be("Hyper-V KVP");
        source.Priority.Should().Be(50);
        source.RequiresNetwork.Should().BeFalse();
    }

    [SupportedOSPlatform("windows")]
    private static bool HyperVRegistryExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HyperVKvpDataSource.HyperVGuestKey);
            return key?.GetValue(HyperVKvpDataSource.VirtualMachineIdValue) is string s && !string.IsNullOrEmpty(s);
        }
        catch
        {
            return false;
        }
    }
}
