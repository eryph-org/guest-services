using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Stages;

public sealed class StageRunner(
    IDataSourceLocator dataSourceLocator,
    IUserDataPipeline userDataPipeline,
    IStateStore stateStore,
    IEnumerable<IModule> modules,
    IReportingDispatcher reporter,
    IWindowsOs os,
    ILogger<StageRunner> logger) : IStageRunner
{
    private static readonly Stage[] StageOrder =
    [
        Stage.Local,
        Stage.Network,
        Stage.Config,
        Stage.Final,
    ];

    public Task<StageRunOutcome> RunAsync(CancellationToken cancellationToken) =>
        RunStagesAsync(StageOrder, cancellationToken);

    public Task<StageRunOutcome> RunStageAsync(Stage stage, CancellationToken cancellationToken) =>
        RunStagesAsync([stage], cancellationToken);

    private async Task<StageRunOutcome> RunStagesAsync(
        IReadOnlyList<Stage> stagesToRun,
        CancellationToken cancellationToken)
    {
        // Pre-step: discover the datasource before any stage runs.
        var data = await dataSourceLocator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            logger.LogWarning("No data source available; nothing to provision");
            return StageRunOutcome.NoDataSource.Instance;
        }

        var state = await LoadOrResetStateAsync(data.InstanceId, cancellationToken).ConfigureAwait(false);
        await reporter.EmitAsync(
            new ReportingEvent.ProvisioningStarted(data.InstanceId) { Origin = "stage-runner" },
            cancellationToken).ConfigureAwait(false);

        var context = new ModuleContext(os, data);
        var buckets = BuildStageBuckets(modules);

        // userData is resolved lazily at the start of the Network stage so that
        // any platform setup done in Local (e.g. raising the network) can
        // influence subsequent stages without paying the parse cost up front.
        ResolvedUserData? resolvedUserData = null;

        // When a single stage is requested via RunStageAsync, the Network
        // stage may not be included — in that case we still need to resolve
        // user-data because Config/Final handlers depend on it.
        var needsUserData = stagesToRun.Any(s => s != Stage.Local);

        foreach (var stage in stagesToRun)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stageName = stage.ToString();

            if (!buckets.TryGetValue(stage, out var stageModules))
                stageModules = [];

            await reporter.EmitAsync(
                new ReportingEvent.StageStarted(stage) { Origin = $"stage:{stageName}" },
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Running stage {Stage} ({Count} module(s))", stageName, stageModules.Count);

            // Resolve user-data once, at the start of the first non-Local stage.
            // (RunStageAsync may start at Network, Config or Final directly, so
            // we resolve on demand rather than gating on Stage.Network only.)
            if (needsUserData && resolvedUserData is null && stage != Stage.Local)
            {
                try
                {
                    resolvedUserData = await userDataPipeline
                        .ResolveAsync(data.GetUserDataBytes(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to resolve user-data");
                    await reporter.EmitAsync(
                        new ReportingEvent.ProvisioningFailed($"userdata-parse: {ex.Message}", ex)
                        {
                            Origin = "stage-runner",
                        },
                        cancellationToken).ConfigureAwait(false);
                    return new StageRunOutcome.Failed("Failed to parse cloud-config userdata", ex);
                }
            }

            // v1 has no Local-stage modules; this placeholder lets modules that
            // happen to be tagged Local still execute, even though we have no
            // resolved user-data yet.
            var userDataForStage = resolvedUserData ?? ResolvedUserData.Empty(new CloudConfigModel());

            foreach (var module in stageModules)
            {
                var moduleKey = module.GetType().FullName ?? module.GetType().Name;
                var moduleName = module.GetType().Name;
                if (state.CompletedHandlers.Contains(moduleKey))
                {
                    logger.LogDebug("Skipping already-completed module {Module}", moduleKey);
                    continue;
                }

                await reporter.EmitAsync(
                    new ReportingEvent.ModuleStarted(moduleName) { Origin = $"module:{moduleName}" },
                    cancellationToken).ConfigureAwait(false);

                ModuleOutcome outcome;
                try
                {
                    outcome = await module.ApplyAsync(userDataForStage, context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Module {Module} threw", moduleKey);
                    await reporter.EmitAsync(
                        new ReportingEvent.ModuleFailed(moduleName, ex.Message, ex)
                        {
                            Origin = $"module:{moduleName}",
                        },
                        cancellationToken).ConfigureAwait(false);
                    await reporter.EmitAsync(
                        new ReportingEvent.ProvisioningFailed($"{moduleKey}: {ex.Message}", ex)
                        {
                            Origin = "stage-runner",
                        },
                        cancellationToken).ConfigureAwait(false);
                    return new StageRunOutcome.Failed($"Module {moduleKey} threw", ex);
                }

                switch (outcome)
                {
                    case ModuleOutcome.Completed:
                        state = state with
                        {
                            CompletedHandlers = AddTo(state.CompletedHandlers, moduleKey),
                            LastUpdated = DateTimeOffset.UtcNow,
                        };
                        await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                        await reporter.EmitAsync(
                            new ReportingEvent.ModuleFinished(moduleName, nameof(ModuleOutcome.Completed))
                            {
                                Origin = $"module:{moduleName}",
                            },
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case ModuleOutcome.RebootRequested reboot:
                        state = state with
                        {
                            CompletedHandlers = AddTo(state.CompletedHandlers, moduleKey),
                            RebootCount = state.RebootCount + 1,
                            LastUpdated = DateTimeOffset.UtcNow,
                        };
                        await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                        await reporter.EmitAsync(
                            new ReportingEvent.ModuleFinished(moduleName, nameof(ModuleOutcome.RebootRequested))
                            {
                                Origin = $"module:{moduleName}",
                            },
                            cancellationToken).ConfigureAwait(false);
                        await reporter.EmitAsync(
                            new ReportingEvent.RebootRequested(reboot.Reason)
                            {
                                Origin = $"module:{moduleName}",
                            },
                            cancellationToken).ConfigureAwait(false);
                        logger.LogInformation("Module {Module} requested reboot: {Reason}", moduleKey, reboot.Reason);
                        return new StageRunOutcome.RebootRequested(reboot.Reason);

                    case ModuleOutcome.Failed failed:
                        logger.LogError("Module {Module} failed: {Reason}", moduleKey, failed.Reason);
                        await reporter.EmitAsync(
                            new ReportingEvent.ModuleFailed(moduleName, failed.Reason, failed.Exception)
                            {
                                Origin = $"module:{moduleName}",
                            },
                            cancellationToken).ConfigureAwait(false);
                        await reporter.EmitAsync(
                            new ReportingEvent.ProvisioningFailed($"{moduleKey}: {failed.Reason}", failed.Exception)
                            {
                                Origin = "stage-runner",
                            },
                            cancellationToken).ConfigureAwait(false);
                        return new StageRunOutcome.Failed(failed.Reason, failed.Exception);
                }
            }

            state = state with
            {
                CompletedStages = AddTo(state.CompletedStages, stageName),
                LastUpdated = DateTimeOffset.UtcNow,
            };
            await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            await reporter.EmitAsync(
                new ReportingEvent.StageFinished(stage) { Origin = $"stage:{stageName}" },
                cancellationToken).ConfigureAwait(false);
        }

        await reporter.EmitAsync(
            new ReportingEvent.ProvisioningCompleted { Origin = "stage-runner" },
            cancellationToken).ConfigureAwait(false);
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

    // Reflect over each module once, group by stage, and sort by (Order, FullName).
    // The result is reused for every stage in StageOrder.
    private static Dictionary<Stage, List<IModule>> BuildStageBuckets(IEnumerable<IModule> modules)
    {
        var entries = modules
            .Select(m => new
            {
                Module = m,
                Attribute = m.GetType()
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
                    .ThenBy(t => t.Module.GetType().FullName, StringComparer.Ordinal)
                    .Select(t => t.Module)
                    .ToList());
    }
}
