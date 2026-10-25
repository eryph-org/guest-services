using System.Globalization;
using System.Text;
using Eryph.GuestServices.HvDataExchange.Common;
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

    // How many times a failed detail write is retried (once per poll) before the
    // watcher gives up and lets the run go terminal anyway, so a persistently
    // failing write cannot wedge it forever.
    private const int MaxDetailWriteAttempts = 5;

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
        var detailAttempts = 0;
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
                    var terminalFailed = state == CloudInitStateMapper.Failed
                        && CloudInitStateMapper.IsTerminal(probe.Status);

                    // On a failed run, surface cloud-init's error summary BEFORE
                    // writing the failed state, so a host poller that sees "failed"
                    // already has the reason. A transient failure here must not be
                    // swallowed (that would lose the reason), so we only let the
                    // run go terminal once the detail is on the wire — bounded, so
                    // a persistently failing write cannot wedge the watcher.
                    var detailReady = true;
                    if (terminalFailed)
                    {
                        detailReady = await EmitFailureDetailAsync(probe, cancellationToken).ConfigureAwait(false);
                        if (!detailReady)
                            detailAttempts++;
                    }

                    // Only advance lastWritten on a successful write, so a
                    // transient KVP failure does not permanently suppress retries.
                    if (state is not null
                        && state != lastWritten
                        && await WriteStateAsync(state, cancellationToken).ConfigureAwait(false))
                    {
                        lastWritten = state;
                    }

                    // Stop only once the terminal state is on the wire AND, for a
                    // failed run, the detail too (or the retry budget is spent).
                    if (CloudInitStateMapper.IsTerminal(probe.Status)
                        && state == lastWritten
                        && (detailReady || detailAttempts >= MaxDetailWriteAttempts))
                    {
                        return;
                    }
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
    // Returns true when the failure detail is on the wire for this boot (freshly
    // written or already present). Returns false on a transient failure so the
    // caller retries before declaring the run terminal — a swallowed failure
    // would lose the reason permanently.
    private async Task<bool> EmitFailureDetailAsync(CloudInitProbe probe, CancellationToken cancellationToken)
    {
        try
        {
            var guestData = await _kvp.GetGuestDataAsync().ConfigureAwait(false);
            var incarnation = ResolveIncarnation(guestData);

            // Write the event FIRST, in its own call: the event is the priority,
            // and keeping it separate from the sweep means a bad pre-existing key
            // in the sweep set cannot veto it (SetGuestValues validates deletes
            // too). Idempotent: skip when this boot's detail is already present.
            if (!DetailAlreadyPresent(guestData, incarnation))
            {
                var entries = EncodeSingleEntry(ComposeFailureMessage(probe), incarnation);
                var eventValues = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var entry in entries)
                    eventValues[entry.Key] = entry.Value;
                await _kvp.SetGuestValuesAsync(eventValues).ConfigureAwait(false);
            }

            // Sweep strictly-older CLOUD_INIT entries (prior boots) best-effort,
            // separately, so a sweep failure cannot lose the event just written.
            await SweepOlderEntriesAsync(guestData, incarnation).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write cloud-init failure detail to KVP; will retry");
            return false;
        }
    }

    // Deletes CLOUD_INIT entries strictly older than the current incarnation
    // (prior boots) so the pool does not grow unbounded across reboots. Relative
    // comparison only — never a wall-clock one — so a clock stepped after
    // cloud-init wrote its entries cannot delete them. A key that would not pass
    // validation is skipped so it cannot veto the whole delete batch.
    private async Task SweepOlderEntriesAsync(IReadOnlyDictionary<string, string> guestData, long incarnation)
    {
        var stale = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in guestData.Keys)
        {
            if (TryParseCloudInitKey(key, out var keyIncarnation, out _)
                && keyIncarnation < incarnation
                && DataValidator.IsKeyValid(key, out _))
            {
                stale[key] = null;
            }
        }

        if (stale.Count == 0)
            return;

        try
        {
            await _kvp.SetGuestValuesAsync(stale).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to sweep stale CLOUD_INIT entries");
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

            var cut = SafeCutLength(message, Math.Max(0, message.Length * 3 / 4));
            message = cut == 0 ? string.Empty : message[..cut] + "...(truncated)";
        }
    }

    // Largest length <= max that does not split a UTF-16 surrogate pair, so a cut
    // never leaves a lone surrogate (which the JSON writer emits as U+FFFD).
    private static int SafeCutLength(string text, int max)
    {
        var n = Math.Min(max, text.Length);
        if (n > 0 && n < text.Length && char.IsHighSurrogate(text[n - 1]))
            n--;
        return n;
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

        if (builder.Length <= MaxMessageChars)
            return builder.ToString();

        var text = builder.ToString();
        return text[..SafeCutLength(text, MaxMessageChars)] + "...(truncated; see the guest's cloud-init logs)";
    }

    // The incarnation to write under: the highest present on a CLOUD_INIT entry
    // that cloud-init itself wrote (any name other than our own detail event), so
    // reusing it keeps both sets under the host's highest-incarnation rule.
    // Deliberately ignores our OWN prior-boot detail event — reusing it would make
    // DetailAlreadyPresent suppress THIS boot's failure. Falls back to this boot's
    // computed incarnation when cloud-init wrote nothing this boot.
    private long ResolveIncarnation(IReadOnlyDictionary<string, string> guestData)
    {
        long? highest = null;
        foreach (var key in guestData.Keys)
        {
            if (TryParseCloudInitKey(key, out var keyIncarnation, out var name)
                && !string.Equals(name, DetailEventName, StringComparison.Ordinal)
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

    // Parses a CLOUD_INIT key with the SAME shape the host decoder requires
    // (CLOUD_INIT|<incarnation>|<type>|<name>|<uuid>[|<index>], 5 or 6 segments),
    // so a malformed key cannot set a bogus "highest" incarnation the host never
    // sees — which would otherwise make egs write at that incarnation and hide
    // cloud-init's real events.
    private static bool TryParseCloudInitKey(string key, out long incarnation, out string name)
    {
        incarnation = 0;
        name = string.Empty;

        var parts = key.Split('|');
        if (parts.Length is < 5 or > 6)
            return false;
        if (!string.Equals(parts[0], CloudInitKvpEventEncoder.KeyPrefix, StringComparison.Ordinal))
            return false;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out incarnation))
            return false;

        name = parts[3];
        return true;
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
