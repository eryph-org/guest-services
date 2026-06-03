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
                var cloudInitStatus = await _reader.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (cloudInitStatus is null)
                {
                    _logger.LogDebug("cloud-init is not available; not reporting provisioning state");
                    return;
                }

                var state = CloudInitStateMapper.Map(cloudInitStatus);
                if (state is not null && state != lastWritten)
                {
                    await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
                    lastWritten = state;
                }

                if (CloudInitStateMapper.IsTerminal(cloudInitStatus))
                    return;

                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service stopping.
        }
    }

    private async Task WriteStateAsync(string state, CancellationToken cancellationToken)
    {
        try
        {
            await _kvp.SetGuestValuesAsync(
                new Dictionary<string, string?>(StringComparer.Ordinal) { [StateKey] = state })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write {Key} to KVP; host-side reporting will be stale", StateKey);
        }
    }
}
