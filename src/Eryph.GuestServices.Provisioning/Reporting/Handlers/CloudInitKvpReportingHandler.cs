using System.Globalization;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.State;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Reporting.Handlers;

// Emits the same CLOUD_INIT|... KVP event stream that real cloud-init's
// HyperVKvpReportingHandler writes, so a host-side reader parses Windows (egs)
// guests the same way it parses Linux (real cloud-init) guests. Runs ALONGSIDE
// KvpReportingHandler, which keeps writing the eryph.provisioning.* snapshot —
// this handler does not touch it.
//
// Like its sibling it probes once at construction and gates via IsApplicable;
// KVP write failures during a run are warn-logged and swallowed (the host-side
// reader is optional).
internal sealed class CloudInitKvpReportingHandler : IReportingHandler
{
    // Not CLOUD_INIT|... so it is never mistaken for an event nor swept.
    private const string ProbeKey = "CLOUD_INIT.probe";

    private readonly IGuestDataExchange _kvp;
    private readonly IStateStore _stateStore;
    private readonly IVmIdProvider _vmIdProvider;
    private readonly ILogger<CloudInitKvpReportingHandler> _logger;
    private readonly bool _isApplicable;

    // The cloud-init name of the running stage, used to scope module child
    // events. Updated as stages start.
    private string? _currentStage;
    private int? _incarnation;
    private string? _vmId;
    private bool _sweptStale;

    public CloudInitKvpReportingHandler(
        IGuestDataExchange kvp,
        IStateStore stateStore,
        IVmIdProvider vmIdProvider,
        ILogger<CloudInitKvpReportingHandler> logger)
    {
        _kvp = kvp;
        _stateStore = stateStore;
        _vmIdProvider = vmIdProvider;
        _logger = logger;
        _isApplicable = Probe();
    }

    public bool IsApplicable => _isApplicable;

    public async Task PublishAsync(ReportingEvent reportingEvent, CancellationToken cancellationToken)
    {
        // Track the running stage before mapping so module events nest under it.
        if (reportingEvent is ReportingEvent.StageStarted started)
            _currentStage = CloudInitKvpEventEncoder.MapStageName(started.Stage);

        var mapped = CloudInitKvpEventEncoder.Map(reportingEvent, _currentStage);
        if (mapped is null)
            return;

        // Resolve incarnation / vm_id and sweep stale entries only once we have
        // an event that actually produces output — events with no cloud-init
        // analogue (ProvisioningStarted, Progress, SshHostKeysReported, ...) must
        // not trigger the KVP reads/writes of initialization.
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var entry = CloudInitKvpEventEncoder.Encode(
            mapped.Value, _incarnation ?? 0, _vmId ?? "", Guid.NewGuid());

        try
        {
            await _kvp.SetGuestValuesAsync(
                new Dictionary<string, string?>(StringComparer.Ordinal) { [entry.Key] = entry.Value })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to write cloud-init KVP event for {Event}; host-side reporting will be stale",
                reportingEvent.GetType().Name);
        }
    }

    // Resolved lazily on the first published event: the incarnation (the
    // per-instance reboot count) and vm_id are stable for the run, and the
    // stale-incarnation sweep needs the current incarnation first.
    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_incarnation is null)
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            _incarnation = state?.RebootCount ?? 0;
        }

        _vmId ??= _vmIdProvider.GetVmId();

        if (!_sweptStale)
        {
            _sweptStale = true;
            await SweepStaleIncarnationsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Cloud-init bumps the incarnation per boot and sweeps prior entries so the
    // pool stays bounded. We do the same: delete any CLOUD_INIT|... entry whose
    // incarnation is not the current one (the write path deletes on a null value).
    private async Task SweepStaleIncarnationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _kvp.GetGuestDataAsync().ConfigureAwait(false);
            var stale = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var key in existing.Keys)
            {
                if (!key.StartsWith(CloudInitKvpEventEncoder.KeyPrefix + "|", StringComparison.Ordinal))
                    continue;

                var parts = key.Split('|');
                if (parts.Length >= 2
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var incarnation)
                    && incarnation != _incarnation)
                {
                    stale[key] = null;
                }
            }

            if (stale.Count > 0)
                await _kvp.SetGuestValuesAsync(stale).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sweep stale CLOUD_INIT incarnations");
        }
    }

    // Synchronous probe so IsApplicable stays a sync property (per the interface).
    // We block on the async write because this runs once at startup and touches a
    // local registry key on Windows.
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
            _logger.LogWarning(ex, "Hyper-V KVP not available; CloudInitKvpReportingHandler will be disabled");
            return false;
        }
    }
}
