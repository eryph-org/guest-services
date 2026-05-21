using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class DataSourceLocator : IDataSourceLocator
{
    public static readonly TimeSpan DefaultMaxWaitPerDataSource = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyList<IDataSource> _dataSources;
    private readonly ILogger<DataSourceLocator> _logger;
    private readonly TimeSpan _maxWaitPerDataSource;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Track which datasource produced the last located result so OnProvisioningCompletedAsync
    // can dispatch back to it.
    private readonly Dictionary<DataSourceResult, IDataSource> _completionMap = new();
    private readonly object _completionMapLock = new();

    public DataSourceLocator(
        IEnumerable<IDataSource> dataSources,
        ILogger<DataSourceLocator> logger)
        : this(dataSources, logger, DefaultMaxWaitPerDataSource, Task.Delay)
    {
    }

    // Test seam: caller-supplied delay function (and total-wait cap) for fast unit tests.
    internal DataSourceLocator(
        IEnumerable<IDataSource> dataSources,
        ILogger<DataSourceLocator> logger,
        TimeSpan maxWaitPerDataSource,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _dataSources = dataSources.OrderBy(d => d.Priority).ToArray();
        _logger = logger;
        _maxWaitPerDataSource = maxWaitPerDataSource;
        _delay = delay;
    }

    public async Task<DataSourceResult?> LocateAsync(CancellationToken cancellationToken)
    {
        foreach (var source in _dataSources)
        {
            _logger.LogDebug(
                "Probing data source {Name} (priority {Priority})",
                source.Name,
                source.Priority);

            var data = await ProbeWithRetryAsync(source, cancellationToken).ConfigureAwait(false);
            if (data is null)
                continue;

            lock (_completionMapLock)
            {
                _completionMap[data] = source;
            }

            _logger.LogInformation(
                "Data source {Name} produced result for instance {InstanceId}",
                source.Name,
                data.InstanceId);
            return data;
        }

        _logger.LogWarning("No data source produced provisioning data");
        return null;
    }

    public Task OnProvisioningCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        IDataSource? source;
        lock (_completionMapLock)
        {
            _completionMap.TryGetValue(data, out source);
        }

        if (source is null)
        {
            _logger.LogDebug(
                "OnProvisioningCompletedAsync called for a data source result that was not produced by this locator");
            return Task.CompletedTask;
        }

        return source.OnCompletedAsync(data, cancellationToken);
    }

    private async Task<DataSourceResult?> ProbeWithRetryAsync(
        IDataSource source,
        CancellationToken cancellationToken)
    {
        var totalWaited = TimeSpan.Zero;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DataSourceProbeResult probe;
            try
            {
                probe = await source.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Data source {Name} threw during probe; skipping",
                    source.Name);
                return null;
            }

            switch (probe)
            {
                case DataSourceProbeResult.NotApplicable:
                    return null;

                case DataSourceProbeResult.Ready ready:
                    return ready.Data;

                case DataSourceProbeResult.Failed failed:
                    _logger.LogWarning(
                        failed.Exception,
                        "Data source {Name} failed: {Reason}",
                        source.Name,
                        failed.Reason);
                    return null;

                case DataSourceProbeResult.WaitForReady wait:
                    if (totalWaited + wait.Backoff > _maxWaitPerDataSource)
                    {
                        _logger.LogWarning(
                            "Data source {Name} exceeded max wait of {MaxWait}; giving up. Last reason: {Reason}",
                            source.Name,
                            _maxWaitPerDataSource,
                            wait.Reason);
                        return null;
                    }

                    _logger.LogDebug(
                        "Data source {Name} not ready: {Reason}. Backing off for {Backoff}",
                        source.Name,
                        wait.Reason,
                        wait.Backoff);
                    await _delay(wait.Backoff, cancellationToken).ConfigureAwait(false);
                    totalWaited += wait.Backoff;
                    continue;

                default:
                    _logger.LogWarning(
                        "Data source {Name} returned unknown probe result {Type}",
                        source.Name,
                        probe.GetType().Name);
                    return null;
            }
        }
    }
}
