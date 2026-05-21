using System.Globalization;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Reporting.Handlers;

// Legacy KVP key scheme (preserved verbatim — eryph host-side tools read these):
//   eryph.provisioning.state          : started | running | reboot_pending | completed | failed
//   eryph.provisioning.instance       : <instance-id>
//   eryph.provisioning.stage          : <stage-name>
//   eryph.provisioning.reboot_reason  : <reason>          (set on RebootRequested, cleared otherwise)
//   eryph.provisioning.error          : <error>           (set on ProvisioningFailed, cleared otherwise)
//   eryph.provisioning.updated        : ISO-8601 UTC timestamp (every event)
//
// Writes to a non-Hyper-V host are pointless and may throw; the handler probes
// once at construction and gates itself via IsApplicable. KVP write failures
// during normal operation are warn-logged and swallowed — the host-side reader
// is optional.
internal sealed class KvpReportingHandler : IReportingHandler
{
    private const string StateKey = "eryph.provisioning.state";
    private const string InstanceKey = "eryph.provisioning.instance";
    private const string StageKey = "eryph.provisioning.stage";
    private const string RebootReasonKey = "eryph.provisioning.reboot_reason";
    private const string ErrorKey = "eryph.provisioning.error";
    private const string UpdatedKey = "eryph.provisioning.updated";
    private const string ProbeKey = "eryph.provisioning.handler.ready";

    private readonly IGuestDataExchange _kvp;
    private readonly ILogger<KvpReportingHandler> _logger;
    private readonly bool _isApplicable;

    public KvpReportingHandler(IGuestDataExchange kvp, ILogger<KvpReportingHandler> logger)
    {
        _kvp = kvp;
        _logger = logger;
        _isApplicable = Probe();
    }

    public bool IsApplicable => _isApplicable;

    public async Task PublishAsync(ReportingEvent reportingEvent, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [UpdatedKey] = reportingEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };

        switch (reportingEvent)
        {
            case ReportingEvent.ProvisioningStarted started:
                values[StateKey] = "started";
                values[InstanceKey] = started.InstanceId;
                values[ErrorKey] = null;
                values[RebootReasonKey] = null;
                break;

            case ReportingEvent.StageStarted stageStarted:
                values[StateKey] = "running";
                values[StageKey] = stageStarted.Stage.ToString();
                break;

            case ReportingEvent.RebootRequested reboot:
                values[StateKey] = "reboot_pending";
                values[RebootReasonKey] = reboot.Reason;
                break;

            case ReportingEvent.ProvisioningCompleted:
                values[StateKey] = "completed";
                values[StageKey] = null;
                values[ErrorKey] = null;
                values[RebootReasonKey] = null;
                break;

            case ReportingEvent.ProvisioningFailed failed:
                values[StateKey] = "failed";
                values[ErrorKey] = failed.Reason;
                break;

            default:
                // Other events only refresh the timestamp.
                break;
        }

        try
        {
            await _kvp.SetGuestValuesAsync(values).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to write KVP values for {Event}; host-side reporting will be stale",
                reportingEvent.GetType().Name);
        }
    }

    // Synchronous probe so IsApplicable can stay a sync property per the interface.
    // We block on the async write because this runs exactly once at startup and
    // touches a local registry key on Windows.
    private bool Probe()
    {
        try
        {
            _kvp.SetGuestValuesAsync(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ProbeKey] = "1",
            }).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Hyper-V KVP not available; KvpReportingHandler will be disabled");
            return false;
        }
    }
}
