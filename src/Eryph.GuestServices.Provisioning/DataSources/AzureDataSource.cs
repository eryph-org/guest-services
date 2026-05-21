using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Eryph.GuestServices.Provisioning.DataSources;

// STUB. Detects an Azure environment via HKLM\SOFTWARE\Microsoft\Windows Azure\VmId
// and signals WaitForReady while Microsoft's Provisioning Agent is still writing
// CustomData.bin. A full implementation will read CustomData.bin and surface the
// VmId-derived instance id; see the reference Python service shipped with the
// cloudbase-init patches at
// templates/windows/cookbooks/packer/files/default/cloudbase-patches/metadata/services/azurecustomdata.py.
public sealed class AzureDataSource(ILogger<AzureDataSource> logger) : IDataSource
{
    internal const string AzureVmIdKey = @"SOFTWARE\Microsoft\Windows Azure";
    internal const string AzureVmIdValue = "VmId";
    internal const string CustomDataPath = @"C:\AzureData\CustomData.bin";

    public string Name => "Azure";

    public int Priority => 10;

    public bool RequiresNetwork => false;

    public Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Probe());

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        // TODO: delete CustomData.bin (and empty parent dir) once full impl lands —
        // mirrors AzureCustomDataService.provisioning_completed() in the reference.
        return Task.CompletedTask;
    }

    private DataSourceProbeResult Probe()
    {
        if (!OperatingSystem.IsWindows())
            return DataSourceProbeResult.NotApplicable.Instance;

        var vmId = ReadAzureVmId();
        if (string.IsNullOrEmpty(vmId))
            return DataSourceProbeResult.NotApplicable.Instance;

        if (!File.Exists(CustomDataPath))
        {
            logger.LogDebug(
                "Azure VmId present but CustomData.bin not yet at {Path}; waiting for PA",
                CustomDataPath);
            return new DataSourceProbeResult.WaitForReady(
                "Azure PA not yet completed",
                TimeSpan.FromSeconds(5));
        }

        // TODO: full implementation reads CustomData.bin and returns Ready(...).
        logger.LogDebug(
            "Azure datasource stub: VmId={VmId}, CustomData.bin present, full impl pending",
            vmId);
        return DataSourceProbeResult.NotApplicable.Instance;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadAzureVmIdCore()
    {
        using var key = Registry.LocalMachine.OpenSubKey(AzureVmIdKey);
        return key?.GetValue(AzureVmIdValue) as string;
    }

    private static string? ReadAzureVmId()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return ReadAzureVmIdCore();
        }
        catch
        {
            return null;
        }
    }
}
