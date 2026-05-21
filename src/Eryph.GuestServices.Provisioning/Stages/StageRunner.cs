using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Stages;

public sealed class StageRunner(
    IDataSourceLocator dataSourceLocator,
    ICloudConfigSerializer serializer,
    IStateStore stateStore,
    IEnumerable<IHandler> handlers,
    IHostStatusReporter reporter,
    IWindowsOs os,
    ILogger<StageRunner> logger) : IStageRunner
{
    private static readonly Stage[] StageOrder =
    [
        Stage.Discovery,
        Stage.Hostname,
        Stage.Users,
        Stage.Files,
        Stage.Commands,
        Stage.Finalize,
    ];

    public async Task<StageRunOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var data = await dataSourceLocator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            logger.LogWarning("No data source available; nothing to provision");
            return StageRunOutcome.NoDataSource.Instance;
        }

        var state = await LoadOrResetStateAsync(data.InstanceId, cancellationToken).ConfigureAwait(false);
        await reporter.ReportStartedAsync(data.InstanceId, cancellationToken).ConfigureAwait(false);

        global::Eryph.GuestServices.CloudConfig.CloudConfig config;
        try
        {
            config = string.IsNullOrWhiteSpace(data.UserData)
                ? new global::Eryph.GuestServices.CloudConfig.CloudConfig()
                : serializer.Deserialize(data.UserData!);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deserialize cloud-config userdata");
            await reporter.ReportFailedAsync($"userdata-parse: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return new StageRunOutcome.Failed("Failed to parse cloud-config userdata", ex);
        }

        var context = new HandlerContext(os, data);
        var buckets = BuildStageBuckets(handlers);

        foreach (var stage in StageOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stageName = stage.ToString();

            if (!buckets.TryGetValue(stage, out var stageHandlers))
                stageHandlers = [];

            logger.LogInformation("Running stage {Stage} ({Count} handler(s))", stageName, stageHandlers.Count);

            foreach (var handler in stageHandlers)
            {
                var handlerKey = handler.GetType().FullName ?? handler.GetType().Name;
                if (state.CompletedHandlers.Contains(handlerKey))
                {
                    logger.LogDebug("Skipping already-completed handler {Handler}", handlerKey);
                    continue;
                }

                HandlerOutcome outcome;
                try
                {
                    outcome = await handler.ApplyAsync(config, context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Handler {Handler} threw", handlerKey);
                    await reporter.ReportFailedAsync($"{handlerKey}: {ex.Message}", cancellationToken).ConfigureAwait(false);
                    return new StageRunOutcome.Failed($"Handler {handlerKey} threw", ex);
                }

                switch (outcome)
                {
                    case HandlerOutcome.Completed:
                        state = state with
                        {
                            CompletedHandlers = AddTo(state.CompletedHandlers, handlerKey),
                            LastUpdated = DateTimeOffset.UtcNow,
                        };
                        await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                        break;

                    case HandlerOutcome.RebootRequested reboot:
                        state = state with
                        {
                            CompletedHandlers = AddTo(state.CompletedHandlers, handlerKey),
                            RebootCount = state.RebootCount + 1,
                            LastUpdated = DateTimeOffset.UtcNow,
                        };
                        await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                        await reporter.ReportRebootPendingAsync(reboot.Reason, cancellationToken).ConfigureAwait(false);
                        logger.LogInformation("Handler {Handler} requested reboot: {Reason}", handlerKey, reboot.Reason);
                        return new StageRunOutcome.RebootRequested(reboot.Reason);

                    case HandlerOutcome.Failed failed:
                        logger.LogError("Handler {Handler} failed: {Reason}", handlerKey, failed.Reason);
                        await reporter.ReportFailedAsync($"{handlerKey}: {failed.Reason}", cancellationToken).ConfigureAwait(false);
                        return new StageRunOutcome.Failed(failed.Reason, failed.Exception);
                }
            }

            state = state with
            {
                CompletedStages = AddTo(state.CompletedStages, stageName),
                LastUpdated = DateTimeOffset.UtcNow,
            };
            await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            await reporter.ReportStageCompletedAsync(stageName, cancellationToken).ConfigureAwait(false);
        }

        await reporter.ReportCompletedAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Provisioning completed for instance {InstanceId}", data.InstanceId);
        return StageRunOutcome.Success.Instance;
    }

    private async Task<ProvisioningState> LoadOrResetStateAsync(string instanceId, CancellationToken cancellationToken)
    {
        var existing = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null && string.Equals(existing.InstanceId, instanceId, StringComparison.Ordinal))
            return existing;

        if (existing is not null)
        {
            logger.LogInformation(
                "Instance id changed from {Old} to {New}; resetting state",
                existing.InstanceId,
                instanceId);
            await stateStore.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var fresh = new ProvisioningState
        {
            InstanceId = instanceId,
            StartedAt = now,
            LastUpdated = now,
        };
        await stateStore.SaveAsync(fresh, cancellationToken).ConfigureAwait(false);
        return fresh;
    }

    private static HashSet<string> AddTo(HashSet<string> source, string value)
    {
        var next = new HashSet<string>(source, source.Comparer) { value };
        return next;
    }

    // Reflect over each handler once, group by stage, and sort by (Order, FullName).
    // The result is reused for every stage in StageOrder.
    private static Dictionary<Stage, List<IHandler>> BuildStageBuckets(IEnumerable<IHandler> handlers)
    {
        var entries = handlers
            .Select(h => new
            {
                Handler = h,
                Attribute = h.GetType()
                    .GetCustomAttributes(typeof(StageAttribute), inherit: false)
                    .OfType<StageAttribute>()
                    .FirstOrDefault(),
            })
            .Where(t => t.Attribute is not null)
            .ToList();

        return entries
            .GroupBy(t => t.Attribute!.Stage)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(t => t.Attribute!.Order)
                    .ThenBy(t => t.Handler.GetType().FullName, StringComparer.Ordinal)
                    .Select(t => t.Handler)
                    .ToList());
    }
}
