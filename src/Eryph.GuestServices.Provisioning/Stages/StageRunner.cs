using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Semaphores;
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
    ISemaphoreStore semaphoreStore,
    IBootSessionDetector bootSessionDetector,
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

        // Per-boot semaphores must be cleared at the start of every new boot.
        // Detection persists a marker so this is a no-op after the first stage
        // run within a single boot.
        if (await bootSessionDetector.IsNewBootAsync(cancellationToken).ConfigureAwait(false))
        {
            await semaphoreStore.ClearPerBootAsync(cancellationToken).ConfigureAwait(false);
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

            if (!buckets.TryGetValue(stage, out var stageEntries))
                stageEntries = [];

            await reporter.EmitAsync(
                new ReportingEvent.StageStarted(stage) { Origin = $"stage:{stageName}" },
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Running stage {Stage} ({Count} module(s))", stageName, stageEntries.Count);

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

            foreach (var entry in stageEntries)
            {
                var module = entry.Module;
                var moduleKey = module.GetType().FullName ?? module.GetType().Name;
                var moduleName = module.GetType().Name;
                var frequency = entry.Frequency;

                if (await semaphoreStore.ExistsAsync(moduleKey, frequency, data.InstanceId, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogInformation(
                        "Skipping module {Module} ({Frequency}); semaphore already present",
                        moduleKey, frequency);
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
                        await semaphoreStore.WriteAsync(moduleKey, frequency, data.InstanceId, "completed", cancellationToken)
                            .ConfigureAwait(false);
                        state = state with
                        {
                            CompletedHandlers = AddPerInstanceIfApplicable(state.CompletedHandlers, moduleKey, frequency),
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
                        // Write the marker BEFORE returning — otherwise the
                        // post-reboot run would re-execute the module and we
                        // would loop forever.
                        await semaphoreStore.WriteAsync(moduleKey, frequency, data.InstanceId, "reboot-requested", cancellationToken)
                            .ConfigureAwait(false);
                        state = state with
                        {
                            CompletedHandlers = AddPerInstanceIfApplicable(state.CompletedHandlers, moduleKey, frequency),
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
                        // Deliberately do NOT write a semaphore on failure;
                        // the module should re-run on the next pass.
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

        // RFC 0005: cleanup hook fires only on full success (every stage in the
        // run completed, no reboot pending). Mirrors cloudbase-init's
        // provisioning_completed() — see init.py:228–232. Reboot-and-continue
        // returns before this point so the datasource stays available across
        // the boot. Failures during cleanup are non-fatal: provisioning has
        // already succeeded by the time we're here, so a stuck CustomData.bin
        // is not worth flipping the run to Failed.
        try
        {
            await dataSourceLocator.OnProvisioningCompletedAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Data source cleanup hook for {Source} threw; provisioning is still considered successful",
                data.SourceName);
        }

        return StageRunOutcome.Success.Instance;
    }

    private async Task<ProvisioningState> LoadOrResetStateAsync(string instanceId, CancellationToken cancellationToken)
    {
        var existing = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null && string.Equals(existing.InstanceId, instanceId, StringComparison.Ordinal))
        {
            // Migration: state.json may carry a CompletedHandlers list from
            // the pre-semaphore release. Promote each entry to a per-instance
            // semaphore so subsequent runs gate the right way. Only write
            // markers that are not already present — avoids surprising the
            // operator if they manually deleted a semaphore.
            await MigrateLegacyCompletedHandlersAsync(existing, cancellationToken).ConfigureAwait(false);
            return existing;
        }

        if (existing is not null)
        {
            logger.LogInformation(
                "Instance id changed from {Old} to {New}; resetting state",
                existing.InstanceId,
                instanceId);
            await stateStore.ResetAsync(cancellationToken).ConfigureAwait(false);
            // Per-instance semaphores live under the OLD instance id; clear
            // them so the new instance starts clean. Per-once survives.
            await semaphoreStore.ClearPerInstanceAsync(existing.InstanceId, cancellationToken).ConfigureAwait(false);
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

    private async Task MigrateLegacyCompletedHandlersAsync(
        ProvisioningState state,
        CancellationToken cancellationToken)
    {
        if (state.CompletedHandlers.Count == 0)
            return;

        var migrated = 0;
        foreach (var moduleKey in state.CompletedHandlers)
        {
            if (await semaphoreStore.ExistsAsync(moduleKey, ModuleFrequency.PerInstance, state.InstanceId, cancellationToken).ConfigureAwait(false))
                continue;
            await semaphoreStore.WriteAsync(
                moduleKey,
                ModuleFrequency.PerInstance,
                state.InstanceId,
                "migrated-from-state.json",
                cancellationToken).ConfigureAwait(false);
            migrated++;
        }

        if (migrated > 0)
            logger.LogInformation(
                "Migrated {Count} legacy CompletedHandlers entries to per-instance semaphores",
                migrated);
    }

    private static HashSet<string> AddTo(HashSet<string> source, string value)
    {
        var next = new HashSet<string>(source, source.Comparer) { value };
        return next;
    }

    // Only per-instance modules are mirrored into state.json's CompletedHandlers.
    // Per-boot is by definition not "completed for this instance" — it resets
    // on every reboot — and per-once is a global concept, not per-instance.
    private static HashSet<string> AddPerInstanceIfApplicable(
        HashSet<string> source,
        string moduleKey,
        ModuleFrequency frequency)
    {
        return frequency == ModuleFrequency.PerInstance ? AddTo(source, moduleKey) : source;
    }

    // Reflect over each module once, group by stage, and sort by (Order, FullName).
    // The result is reused for every stage in StageOrder.
    private static Dictionary<Stage, List<StageEntry>> BuildStageBuckets(IEnumerable<IModule> modules)
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
                    .Select(t => new StageEntry(t.Module, t.Attribute!.Frequency))
                    .ToList());
    }

    private readonly record struct StageEntry(IModule Module, ModuleFrequency Frequency);
}
