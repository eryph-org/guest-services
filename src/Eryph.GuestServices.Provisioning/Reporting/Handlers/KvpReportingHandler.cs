using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Reporting.Handlers;

// The simple provisioning-status key (RFC 0031, "Surface 2"):
//   eryph.provisioning.state : started | running | reboot_pending | completed | failed
//
// This is the ONLY status key. The rich reporting — per-stage events and, in
// particular, the failure reason — lives in the cloud-init CLOUD_INIT|... event
// stream (CloudInitKvpReportingHandler on Windows; real cloud-init on Linux), so
// a host reader reads the reason the same way on both OSes. The old bespoke
// keys (instance/stage/reboot_reason/error/updated) were Windows-only noise and
// have been removed.
//
// Exception: eryph.provisioning.ssh_host_keys is NOT status — it is the SSH
// host-key publication feature (RFC 0018) and is left here for now.
//
// Writes to a non-Hyper-V host are pointless and may throw; the handler probes
// once at construction and gates itself via IsApplicable. KVP write failures
// during normal operation are warn-logged and swallowed — the host-side reader
// is optional.
internal sealed class KvpReportingHandler : IReportingHandler
{
    private const string StateKey = "eryph.provisioning.state";
    private const string SshHostKeysKey = "eryph.provisioning.ssh_host_keys";
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
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        switch (reportingEvent)
        {
            case ReportingEvent.ProvisioningStarted:
                values[StateKey] = "started";
                break;

            case ReportingEvent.StageStarted:
                values[StateKey] = "running";
                break;

            case ReportingEvent.RebootRequested:
                values[StateKey] = "reboot_pending";
                break;

            case ReportingEvent.ProvisioningCompleted:
                values[StateKey] = "completed";
                break;

            case ReportingEvent.ProvisioningFailed:
                // No reason here — the failure reason is carried by the
                // CLOUD_INIT|... FAIL event (RFC 0031, Surface 1), uniform with
                // how cloud-init reports it on Linux.
                values[StateKey] = "failed";
                break;

            case ReportingEvent.SshHostKeysReported sshHostKeys:
                // Compact "type=fingerprint" pairs joined by ';' — keeps the
                // KVP value a single readable string the host-side reader can
                // split without parsing structured data.
                values[SshHostKeysKey] = string.Join(
                    ";",
                    sshHostKeys.Fingerprints.Select(f => $"{f.KeyType}={f.Fingerprint}"));
                break;

            default:
                // Events that don't change the simple status (module start/finish,
                // stage finish, progress, ...) are reflected in the CLOUD_INIT|...
                // stream, not here. Nothing to write.
                return;
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
