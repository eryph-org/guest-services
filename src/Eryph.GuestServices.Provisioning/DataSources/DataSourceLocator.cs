using Eryph.GuestServices.Provisioning.Configuration;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources;

/// <summary>
/// Discovers the active datasource in cloud-init's priority order. Differs from
/// cloud-init's "block on each source in turn" loop in two ways, both deliberate:
///
/// <list type="bullet">
///   <item>
///     <description>The total budget (<see cref="DataSourceSettings.ReadinessTimeoutMinutes"/>)
///     is shared across all sources. We don't want a slow Azure PA to eat the whole
///     deadline while NoCloud is sitting there with the answer.</description>
///   </item>
///   <item>
///     <description>Sources that report <c>WaitForReady</c> are interleaved: we probe
///     lower-priority sources during the wait so a stuck high-priority source can't
///     starve us. cloud-init has the same problem but solves it with platform-specific
///     timeouts; we treat it uniformly. See RFC 0004.</description>
///   </item>
/// </list>
/// </summary>
public sealed class DataSourceLocator : IDataSourceLocator
{
    public static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultMinBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IDataSource> _dataSources;
    private readonly ILogger<DataSourceLocator> _logger;
    private readonly TimeSpan _readinessTimeout;
    private readonly TimeSpan _minBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Track which datasource produced the last located result so OnProvisioningCompletedAsync
    // can dispatch back to it.
    private readonly Dictionary<DataSourceResult, IDataSource> _completionMap = new();
    private readonly object _completionMapLock = new();

    public DataSourceLocator(
        IEnumerable<IDataSource> dataSources,
        ProvisioningSettings settings,
        ILogger<DataSourceLocator> logger)
        : this(
            dataSources,
            logger,
            TimeSpan.FromMinutes(Math.Max(1, settings.DataSources.ReadinessTimeoutMinutes)),
            TimeSpan.FromSeconds(Math.Max(0, settings.DataSources.MinBackoffSeconds)),
            TimeSpan.FromSeconds(Math.Max(1, settings.DataSources.MaxBackoffSeconds)),
            Task.Delay,
            settings.DataSources.DataSourceList)
    {
    }

    // Test seam: explicit timings + injectable delay. Used by both the locator's
    // own unit tests and by composition-root tests that don't want to pay real
    // wall-clock waits. The optional dataSourceList mirrors cloud-init's
    // datasource_list; when supplied it filters/reorders the probe set, otherwise
    // all sources are probed in Priority order.
    internal DataSourceLocator(
        IEnumerable<IDataSource> dataSources,
        ILogger<DataSourceLocator> logger,
        TimeSpan readinessTimeout,
        TimeSpan minBackoff,
        TimeSpan maxBackoff,
        Func<TimeSpan, CancellationToken, Task> delay,
        IReadOnlyList<string>? dataSourceList = null)
    {
        _logger = logger;
        _dataSources = ResolveProbeOrder(dataSources, dataSourceList, logger);
        _readinessTimeout = readinessTimeout;
        _minBackoff = minBackoff;
        _maxBackoff = maxBackoff < minBackoff ? minBackoff : maxBackoff;
        _delay = delay;
    }

    // Builds the resolved probe order. When dataSourceList is null/empty we keep the
    // historical behaviour: all registered sources, ordered by Priority. When it is
    // set we honour cloud-init's datasource_list semantics: probe only the named
    // sources, in the listed order, matching case-insensitively on IDataSource.Name.
    // Unknown names are logged at Warning and skipped. If every name is unknown we
    // fall back to all-by-Priority so a fully-typo'd list can't silently disable
    // provisioning.
    private static IReadOnlyList<IDataSource> ResolveProbeOrder(
        IEnumerable<IDataSource> dataSources,
        IReadOnlyList<string>? dataSourceList,
        ILogger<DataSourceLocator> logger)
    {
        var registered = dataSources.ToArray();

        if (dataSourceList is null || dataSourceList.Count == 0)
            return registered.OrderBy(d => d.Priority).ToArray();

        var resolved = new List<IDataSource>(dataSourceList.Count);
        foreach (var name in dataSourceList)
        {
            var match = registered.FirstOrDefault(
                d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                logger.LogWarning(
                    "Configured datasource {Name} has no matching registered source; ignoring",
                    name);
                continue;
            }

            // Tolerate duplicate names in the configured list — probe each source once.
            if (!resolved.Contains(match))
                resolved.Add(match);
        }

        if (resolved.Count == 0)
        {
            logger.LogWarning(
                "Configured datasource list matched no registered sources; falling back to all sources in priority order");
            return registered.OrderBy(d => d.Priority).ToArray();
        }

        return resolved;
    }

    // Legacy single-cap convenience overload retained for tests that pre-date the
    // split into readiness + min/max backoff. Maps the single cap onto the total
    // readiness timeout and uses default backoff bounds.
    internal DataSourceLocator(
        IEnumerable<IDataSource> dataSources,
        ILogger<DataSourceLocator> logger,
        TimeSpan readinessTimeout,
        Func<TimeSpan, CancellationToken, Task> delay)
        : this(dataSources, logger, readinessTimeout, DefaultMinBackoff, DefaultMaxBackoff, delay)
    {
    }

    public async Task<DataSourceResult?> LocateAsync(CancellationToken cancellationToken)
    {
        // Per-source state for the WaitForReady backoff schedule. We mutate this
        // on every probe so each retry doubles the wait up to _maxBackoff.
        var states = _dataSources
            .Select(s => new SourceState(s))
            .ToList();

        // Virtual budget: we accumulate sleep time rather than reading a real
        // clock. Reasons:
        //   1) Tests inject a synthetic delay (often Task.CompletedTask) — a
        //      real Stopwatch would either spin forever or be dominated by
        //      loop overhead, making the loop's backoff semantics untestable.
        //   2) The probe itself may block for non-trivial wall-clock time
        //      (HTTP IMDS call, registry probe). With virtual time the only
        //      thing that counts against the budget is the *intentional*
        //      WaitForReady sleep — same model cloud-init uses ("waited
        //      ${TOTAL}s of ${BUDGET}s").
        var elapsed = TimeSpan.Zero;
        var seenTransitions = new Dictionary<string, DataSourceProbeResult>(StringComparer.Ordinal);

        while (states.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (elapsed >= _readinessTimeout)
            {
                _logger.LogWarning(
                    "Data source discovery exhausted readiness budget of {Timeout}; giving up. Pending: {Pending}",
                    _readinessTimeout,
                    string.Join(", ", states.Select(s => s.Source.Name)));
                return null;
            }

            // Probe every still-eligible source whose backoff has elapsed. Sources
            // are visited in priority order so a Ready from a higher-priority
            // source short-circuits before we wait on a lower-priority one.
            var stillPending = new List<SourceState>();
            foreach (var state in states)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (state.NextProbeAt > elapsed)
                {
                    stillPending.Add(state);
                    continue;
                }

                var probe = await ProbeOnceAsync(state.Source, cancellationToken).ConfigureAwait(false);
                LogTransition(state.Source, probe, seenTransitions);

                switch (probe)
                {
                    case DataSourceProbeResult.NotApplicable:
                        continue;

                    case DataSourceProbeResult.Ready ready:
                        lock (_completionMapLock)
                            _completionMap[ready.Data] = state.Source;
                        _logger.LogInformation(
                            "Data source {Name} produced result for instance {InstanceId} after {Elapsed}",
                            state.Source.Name,
                            ready.Data.InstanceId,
                            elapsed);
                        return ready.Data;

                    case DataSourceProbeResult.Failed failed:
                        _logger.LogWarning(
                            failed.Exception,
                            "Data source {Name} failed: {Reason}",
                            state.Source.Name,
                            failed.Reason);
                        continue;

                    case DataSourceProbeResult.WaitForReady wait:
                        var backoff = ComputeBackoff(state, wait.Backoff);
                        state.NextProbeAt = elapsed + backoff;
                        state.RecordBackoff(backoff);
                        state.Attempts++;
                        _logger.LogDebug(
                            "Data source {Name} not ready (attempt {Attempt}): {Reason}. Backing off {Backoff}",
                            state.Source.Name,
                            state.Attempts,
                            wait.Reason,
                            backoff);

                        // If the backoff would push us past the deadline, retire
                        // this source rather than scheduling a probe we know we
                        // can't honour.
                        if (state.NextProbeAt >= _readinessTimeout)
                        {
                            _logger.LogWarning(
                                "Data source {Name} would not be probed again within budget; dropping",
                                state.Source.Name);
                            continue;
                        }

                        stillPending.Add(state);
                        continue;
                }
            }

            states = stillPending;
            if (states.Count == 0)
                break;

            // Wait until either the soonest backoff elapses or the deadline hits.
            var soonest = states.Min(s => s.NextProbeAt);
            var sleep = soonest - elapsed;
            if (sleep > TimeSpan.Zero)
            {
                try
                {
                    await _delay(sleep, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                elapsed += sleep;
            }
            else
            {
                // Defensive: if no source is in the future (e.g. all backoffs
                // just expired), nudge elapsed forward by the minimum backoff
                // so the loop can't spin on the same probe in a tight loop.
                elapsed += _minBackoff;
            }
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

    private async Task<DataSourceProbeResult> ProbeOnceAsync(
        IDataSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Data source {Name} threw during probe; treating as Failed",
                source.Name);
            return new DataSourceProbeResult.Failed($"probe threw: {ex.Message}", ex);
        }
    }

    private TimeSpan ComputeBackoff(SourceState state, TimeSpan requested)
    {
        // Datasource-suggested backoff is the floor; we clamp to [min,max] and
        // double the *previous* backoff so repeated WaitForReady results cause
        // exponential growth (1s, 2s, 4s, 8s, ... capped at max).
        var floor = requested < _minBackoff ? _minBackoff : requested;
        if (state.Attempts == 0)
            return Clamp(floor);

        var grown = TimeSpan.FromTicks(state.LastBackoff.Ticks * 2);
        if (grown < floor)
            grown = floor;
        return Clamp(grown);
    }

    private TimeSpan Clamp(TimeSpan value)
    {
        if (value < _minBackoff)
            value = _minBackoff;
        if (value > _maxBackoff)
            value = _maxBackoff;
        return value;
    }

    private void LogTransition(
        IDataSource source,
        DataSourceProbeResult probe,
        Dictionary<string, DataSourceProbeResult> seen)
    {
        // Information-level whenever a source's result *kind* flips - so an
        // operator looking at logs can tell "the datasource came up" apart from
        // "it never settled" without scanning every Debug-level retry line.
        if (!seen.TryGetValue(source.Name, out var prev))
        {
            seen[source.Name] = probe;
            return;
        }

        if (prev.GetType() != probe.GetType())
        {
            _logger.LogInformation(
                "Data source {Name} state changed from {Previous} to {Current}",
                source.Name,
                prev.GetType().Name,
                probe.GetType().Name);
            seen[source.Name] = probe;
        }
    }

    private sealed class SourceState(IDataSource source)
    {
        public IDataSource Source { get; } = source;
        public TimeSpan NextProbeAt { get; set; } = TimeSpan.Zero;
        public TimeSpan LastBackoff { get; private set; } = TimeSpan.Zero;
        public int Attempts;

        public void RecordBackoff(TimeSpan backoff)
        {
            LastBackoff = backoff;
        }
    }
}
