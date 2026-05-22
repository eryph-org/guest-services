using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Eryph.GuestServices.Provisioning.DataSources;

// STUB. Detects a Hyper-V guest via
// HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters\VirtualMachineId.
// Priority is intentionally last (50) so explicit Azure / EC2 / NoCloud / ConfigDrive
// sources take precedence — KVP is the fallback path used by hosts that ship
// user-data through the Hyper-V data-exchange key-value pair channel.
public sealed class HyperVKvpDataSource(ILogger<HyperVKvpDataSource> logger) : IDataSource
{
    internal const string HyperVGuestKey = @"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters";
    internal const string VirtualMachineIdValue = "VirtualMachineId";

    public string Name => "Hyper-V KVP";

    public int Priority => 50;

    public bool RequiresNetwork => false;

    public Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Probe());

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        // RFC 0005: KVP is host-pushed via the Hyper-V data exchange channel;
        // the guest doesn't own the entries and clearing them would be a no-op
        // anyway (the host can re-push at any time). No-op by design.
        return Task.CompletedTask;
    }

    private DataSourceProbeResult Probe()
    {
        if (!OperatingSystem.IsWindows())
            return DataSourceProbeResult.NotApplicable.Instance;

        var vmId = ReadVirtualMachineId();
        if (string.IsNullOrEmpty(vmId))
            return DataSourceProbeResult.NotApplicable.Instance;

        // TODO: full implementation reads user-data via the existing
        // Eryph.GuestServices.HvDataExchange.Guest plumbing and returns Ready(...).
        logger.LogDebug("Hyper-V KVP datasource stub: VmId={VmId}, full impl pending", vmId);
        return DataSourceProbeResult.NotApplicable.Instance;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadVirtualMachineIdCore()
    {
        using var key = Registry.LocalMachine.OpenSubKey(HyperVGuestKey);
        return key?.GetValue(VirtualMachineIdValue) as string;
    }

    private static string? ReadVirtualMachineId()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return ReadVirtualMachineIdCore();
        }
        catch
        {
            return null;
        }
    }
}
