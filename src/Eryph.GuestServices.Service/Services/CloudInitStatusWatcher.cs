using System.Globalization;
using System.Text;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Reporting.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

// Linux guests run no egs provisioning — cloud-init does. This watcher polls
// cloud-init's status and mirrors it into the single eryph.provisioning.state
// KVP value, so the host reads the same provisioning-state key on Linux and
// Windows. On a failed run it additionally emits one cloud-init-format FAIL
// event carrying cloud-init's own error summary (its status `errors` +
// `recoverable_errors`), so the host-rendered provisioning log names WHAT failed
// without an operator entering the guest. It stops once cloud-init is terminal.
//
// The event is deliberately kept to a SINGLE KVP value: cloud-init already fills
// most of the Hyper-V KVP entry budget with its own ~70 CLOUD_INIT events, and a
// real Ubuntu 24.04 run showed that a multi-chunk event has its trailing chunks
// dropped before they reach the host. We therefore surface cloud-init's concise
// error summary, not the console log — the raw per-script stderr is not
// retrievable here anyway (cloud-init runs user code with capture=False, so it
// only reaches the console log, buried behind the end-of-run banner).
internal sealed class CloudInitStatusWatcher : BackgroundService
{
    private const string StateKey = "eryph.provisioning.state";

    // Keep the whole message within one KVP value (DataValidator caps a value at
    // 1023 chars; the event envelope takes ~120) so the FAIL event is a single
    // entry and survives the KVP budget intact.
    private const int MaxMessageChars = 800;

    // The event name under which the failure detail is written (fixed + short so
    // it never approaches the KVP key-length limit).
    private const string DetailEventName = "modules-final/cloud-init-errors";

    private readonly ICloudInitStatusReader _reader;
    private readonly IGuestDataExchange _kvp;
    private readonly ILogger<CloudInitStatusWatcher> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly long? _bootIncarnationOverride;

    public CloudInitStatusWatcher(
        ICloudInitStatusReader reader,
        IGuestDataExchange kvp,
        ILogger<CloudInitStatusWatcher> logger)
        : this(reader, kvp, logger, TimeSpan.FromSeconds(5))
    {
    }

    internal CloudInitStatusWatcher(
        ICloudInitStatusReader reader,
        IGuestDataExchange kvp,
        ILogger<CloudInitStatusWatcher> logger,
        TimeSpan pollInterval,
        long? bootIncarnationOverride = null)
    {
        _reader = reader;
        _kvp = kvp;
        _logger = logger;
        _pollInterval = pollInterval;
        _bootIncarnationOverride = bootIncarnationOverride;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => WatchAsync(stoppingToken);

    internal async Task WatchAsync(CancellationToken cancellationToken)
    {
        string? lastWritten = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var probe = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                // cloud-init genuinely absent — nothing to mirror, stop.
                if (!probe.Installed)
                {
                    _logger.LogDebug("cloud-init is not installed; not reporting provisioning state");
                    return;
                }

                // Installed but no parseable status yet (cloud-init still
                // starting): keep polling instead of giving up.
                if (probe.Status is not null)
                {
                    var state = CloudInitStateMapper.Map(probe.Status);

                    // On a failed run, surface cloud-init's own error text and the
                    // output-log tail BEFORE writing the failed state, so a host
                    // poller that sees "failed" already has the reason in the log.
                    // Idempotent + best-effort; a failure here never blocks the
                    // state write or the stop.
                    if (state == CloudInitStateMapper.Failed
                        && CloudInitStateMapper.IsTerminal(probe.Status))
                    {
                        await EmitFailureDetailAsync(probe, cancellationToken).ConfigureAwait(false);
                    }

                    // Only advance lastWritten on a successful write, so a
                    // transient KVP failure does not permanently suppress retries.
                    if (state is not null
                        && state != lastWritten
                        && await WriteStateAsync(state, cancellationToken).ConfigureAwait(false))
                    {
                        lastWritten = state;
                    }

                    // Stop only once the terminal state is actually on the wire.
                    // If the terminal write just failed (state != lastWritten),
                    // keep polling and retry rather than returning and leaving the
                    // host stuck on a stale non-terminal state.
                    if (CloudInitStateMapper.IsTerminal(probe.Status) && state == lastWritten)
                        return;
                }

                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service stopping.
        }
    }

    private async Task<bool> WriteStateAsync(string state, CancellationToken cancellationToken)
    {
        try
        {
            await _kvp.SetGuestValuesAsync(
                new Dictionary<string, string?>(StringComparer.Ordinal) { [StateKey] = state })
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write {Key} to KVP; host-side reporting will be stale", StateKey);
            return false;
        }
    }

    // Emits one cloud-init-format FAIL event carrying the failure detail, under
    // the current incarnation, and sweeps strictly-older CLOUD_INIT entries in
    // the same write. Idempotent: re-entering with cloud-init still in "error"
    // (e.g. after an egs restart) does not duplicate the event. Best-effort — a
    // failure here is logged and swallowed.
    private async Task EmitFailureDetailAsync(CloudInitProbe probe, CancellationToken cancellationToken)
    {
        try
        {
            var guestData = await _kvp.GetGuestDataAsync().ConfigureAwait(false);
            var incarnation = ResolveIncarnation(guestData);

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);

            // Sweep CLOUD_INIT entries strictly older than the current incarnation
            // (prior boots) so the pool does not grow unbounded across reboots.
            // Relative comparison only — never a wall-clock one — so a clock that
            // is stepped after cloud-init wrote its entries cannot delete them.
            foreach (var key in guestData.Keys)
            {
                if (TryGetIncarnation(key, out var keyIncarnation) && keyIncarnation < incarnation)
                    values[key] = null;
            }

            // Only emit the detail if it is not already on the wire for this boot.
            if (!DetailAlreadyPresent(guestData, incarnation))
            {
                foreach (var entry in EncodeSingleEntry(ComposeFailureMessage(probe), incarnation))
                    values[entry.Key] = entry.Value;
            }

            if (values.Count > 0)
                await _kvp.SetGuestValuesAsync(values).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write cloud-init failure detail to KVP; host log will lack the reason");
        }
    }

    // Encodes the FAIL event and guarantees a SINGLE KVP entry: the host can drop
    // trailing break-down chunks under KVP pressure, so a multi-entry event would
    // arrive truncated. Budgeting by characters is not enough — the encoder caps
    // the serialized UTF-8 size, and JSON escaping (quotes, newlines, non-ASCII)
    // can push an under-char-limit message over — so we trim and re-encode until
    // it fits one entry.
    private static IReadOnlyList<KeyValuePair<string, string>> EncodeSingleEntry(string message, long incarnation)
    {
        var eventId = Guid.NewGuid();
        while (true)
        {
            var cloudInitEvent = new CloudInitEvent(
                Name: DetailEventName,
                Type: CloudInitKvpEventEncoder.FinishType,
                Result: CloudInitKvpEventEncoder.ResultFail,
                Description: message,
                Timestamp: DateTimeOffset.UtcNow);
            var entries = CloudInitKvpEventEncoder.Encode(cloudInitEvent, incarnation, eventId);
            if (entries.Count <= 1 || message.Length == 0)
                return entries;

            var cut = Math.Max(0, message.Length * 3 / 4);
            message = cut == 0 ? string.Empty : message[..cut] + "...(truncated)";
        }
    }

    // cloud-init's own error summary: its status `errors` (which names the failed
    // module/script) followed by `recoverable_errors`. Capped so the whole event
    // fits one KVP value; the final single-entry guarantee is enforced by
    // EncodeSingleEntry. Falls back to a generic line when cloud-init reported no
    // detail.
    private static string ComposeFailureMessage(CloudInitProbe probe)
    {
        var builder = new StringBuilder();

        if (probe.Errors.Count > 0)
            builder.Append("errors: ").Append(string.Join(" | ", probe.Errors));

        if (probe.RecoverableErrors.Count > 0)
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append("recoverable: ").Append(string.Join(" | ", probe.RecoverableErrors));
        }

        if (builder.Length == 0)
            return "cloud-init reported an error but produced no detail (see the guest's cloud-init logs).";

        return builder.Length <= MaxMessageChars
            ? builder.ToString()
            : builder.ToString(0, MaxMessageChars) + "...(truncated; see the guest's cloud-init logs)";
    }

    // The incarnation to write under: the highest already present on CLOUD_INIT
    // entries (cloud-init's own current-boot entries are the newest, so reusing it
    // keeps both sets under the host's highest-incarnation rule), or this boot's
    // computed incarnation only when the pool holds no CLOUD_INIT entries at all.
    private long ResolveIncarnation(IReadOnlyDictionary<string, string> guestData)
    {
        long? highest = null;
        foreach (var key in guestData.Keys)
        {
            if (TryGetIncarnation(key, out var keyIncarnation)
                && (highest is null || keyIncarnation > highest))
            {
                highest = keyIncarnation;
            }
        }

        return highest ?? _bootIncarnationOverride ?? ComputeBootIncarnation();
    }

    private static bool DetailAlreadyPresent(IReadOnlyDictionary<string, string> guestData, long incarnation)
    {
        var prefix = string.Join(
            '|', CloudInitKvpEventEncoder.KeyPrefix,
            incarnation.ToString(CultureInfo.InvariantCulture),
            CloudInitKvpEventEncoder.FinishType, DetailEventName) + "|";
        foreach (var key in guestData.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryGetIncarnation(string key, out long incarnation)
    {
        incarnation = 0;
        if (!key.StartsWith(CloudInitKvpEventEncoder.KeyPrefix + "|", StringComparison.Ordinal))
            return false;

        var parts = key.Split('|');
        return parts.Length >= 2
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out incarnation);
    }

    // cloud-init's incarnation is the boot time in epoch seconds
    // (int(time.time() - uptime)). Read uptime from /proc/uptime on Linux to
    // match; fall back to the monotonic clock elsewhere / on read failure.
    private static long ComputeBootIncarnation()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var uptimeField = File.ReadAllText("/proc/uptime").Split(' ', 2)[0];
                if (double.TryParse(uptimeField, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)seconds;
            }
        }
        catch
        {
            // Fall through to the monotonic-clock estimate.
        }

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Environment.TickCount64 / 1000;
    }
}
