using Eryph.GuestServices.HvDataExchange.Guest;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

// Linux guests run no egs provisioning — cloud-init does. This watcher polls
// cloud-init's status and mirrors it into the single eryph.provisioning.state
// KVP value, so the host reads the same provisioning-state key on Linux and
// Windows. It writes only that one key and stops once cloud-init is terminal.
internal sealed class CloudInitStatusWatcher : BackgroundService
{
    private const string StateKey = "eryph.provisioning.state";

    private readonly ICloudInitStatusReader _reader;
    private readonly IGuestDataExchange _kvp;
    private readonly ILogger<CloudInitStatusWatcher> _logger;
    private readonly TimeSpan _pollInterval;

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
        TimeSpan pollInterval)
    {
        _reader = reader;
        _kvp = kvp;
        _logger = logger;
        _pollInterval = pollInterval;
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
}
