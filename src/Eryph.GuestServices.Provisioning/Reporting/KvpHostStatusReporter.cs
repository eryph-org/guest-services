using Eryph.GuestServices.HvDataExchange.Guest;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Reporting;

// Writes provisioning progress into the Hyper-V KVP guest pool.
// Keys: eryph.provisioning.state, eryph.provisioning.instance, eryph.provisioning.stage,
//       eryph.provisioning.reboot_reason, eryph.provisioning.error, eryph.provisioning.updated.
public sealed class KvpHostStatusReporter(
    IGuestDataExchange dataExchange,
    ILogger<KvpHostStatusReporter> logger) : IHostStatusReporter
{
    private const string StateKey = "eryph.provisioning.state";
    private const string InstanceKey = "eryph.provisioning.instance";
    private const string StageKey = "eryph.provisioning.stage";
    private const string RebootReasonKey = "eryph.provisioning.reboot_reason";
    private const string ErrorKey = "eryph.provisioning.error";
    private const string UpdatedKey = "eryph.provisioning.updated";

    public Task ReportStartedAsync(string instanceId, CancellationToken cancellationToken) =>
        WriteAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StateKey] = "started",
            [InstanceKey] = instanceId,
            [ErrorKey] = null,
            [RebootReasonKey] = null,
            [UpdatedKey] = Timestamp(),
        });

    public Task ReportStageCompletedAsync(string stage, CancellationToken cancellationToken) =>
        WriteAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StateKey] = "running",
            [StageKey] = stage,
            // Clear any stale failure/reboot markers from a previous run so the host
            // doesn't see error/reboot_reason values that no longer apply.
            [ErrorKey] = null,
            [RebootReasonKey] = null,
            [UpdatedKey] = Timestamp(),
        });

    public Task ReportRebootPendingAsync(string reason, CancellationToken cancellationToken) =>
        WriteAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StateKey] = "reboot_pending",
            [RebootReasonKey] = reason,
            [UpdatedKey] = Timestamp(),
        });

    public Task ReportCompletedAsync(CancellationToken cancellationToken) =>
        WriteAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StateKey] = "completed",
            [ErrorKey] = null,
            [RebootReasonKey] = null,
            [UpdatedKey] = Timestamp(),
        });

    public Task ReportFailedAsync(string error, CancellationToken cancellationToken) =>
        WriteAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StateKey] = "failed",
            [ErrorKey] = error,
            [UpdatedKey] = Timestamp(),
        });

    private async Task WriteAsync(IReadOnlyDictionary<string, string?> values)
    {
        try
        {
            await dataExchange.SetGuestValuesAsync(values).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // KVP write failure is non-fatal; the host may simply not be reading.
            logger.LogDebug(ex, "Failed to write KVP status values");
        }
    }

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");
}
