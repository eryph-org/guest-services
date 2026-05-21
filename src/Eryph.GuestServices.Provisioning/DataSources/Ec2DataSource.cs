using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;

namespace Eryph.GuestServices.Provisioning.DataSources;

// STUB. Detects an EC2 environment via the SMBIOS BIOS vendor (Win32_BIOS.Manufacturer)
// reporting "Amazon EC2". A full implementation will fetch user-data + meta-data
// from the EC2 Instance Metadata Service (IMDSv2 over the link-local 169.254.169.254
// endpoint, requiring a token-based two-step request).
public sealed class Ec2DataSource(ILogger<Ec2DataSource> logger) : IDataSource
{
    internal const string Ec2BiosVendor = "Amazon EC2";

    public string Name => "EC2";

    public int Priority => 20;

    public bool RequiresNetwork => true;

    public Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Probe());

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private DataSourceProbeResult Probe()
    {
        if (!OperatingSystem.IsWindows())
            return DataSourceProbeResult.NotApplicable.Instance;

        var vendor = ReadBiosManufacturer();
        if (string.IsNullOrEmpty(vendor)
            || !vendor.Contains(Ec2BiosVendor, StringComparison.OrdinalIgnoreCase))
        {
            return DataSourceProbeResult.NotApplicable.Instance;
        }

        // TODO: full implementation queries IMDSv2 here and returns Ready(...).
        logger.LogDebug("EC2 datasource stub: BIOS vendor={Vendor}, full impl pending", vendor);
        return DataSourceProbeResult.NotApplicable.Instance;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadBiosManufacturerCore()
    {
        using var session = CimSession.Create(null);
        foreach (var instance in session.EnumerateInstances(@"root\cimv2", "Win32_BIOS"))
        {
            using (instance)
            {
                var value = instance.CimInstanceProperties["Manufacturer"]?.Value;
                if (value is string m)
                    return m;
            }
        }

        return null;
    }

    private static string? ReadBiosManufacturer()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return ReadBiosManufacturerCore();
        }
        catch
        {
            return null;
        }
    }
}
