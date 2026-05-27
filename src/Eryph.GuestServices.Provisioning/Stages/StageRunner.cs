using Eryph.GuestServices.Provisioning.Configuration;
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
    IDataSourceCache dataSourceCache,
    IUserDataPipeline userDataPipeline,
    IStateStore stateStore,
    ISemaphoreStore semaphoreStore,
    IBootSessionDetector bootSessionDetector,
    IEnumerable<IModule> modules,
    IReportingDispatcher reporter,
    IWindowsOs os,
    ProvisioningSettings settings,
    ILogger<StageRunner> logger) : IStageRunner
{
    private static readonly Stage[] StageOrder =
    [
        Stage.Local,
        Stage.Network,
        Stage.Config,
        Stage.Final,
    ];

    // Semaphore outcome values. "completed" gates further runs; any other
    // value (including "reboot-requested") allows the module to re-enter.
    // docs/bugs/0001 explains the bug introduced by treating these as equal.
    internal const string OutcomeCompleted = "completed";
    internal const string OutcomeRebootRequested = "reboot-requested";

    // Cap on how many times the same module may return RebootRequested before
    // the StageRunner stops re-entering it and treats the situation as a failed
    // run. Protects against a misbehaving script that keeps returning 1003
    // without making progress (docs/bugs/0001 "loop-safety"). Sourced from
    // ProvisioningSettings.Reboot.MaxPerModule (default 3).
    private int MaxRebootsPerModule => settings.Reboot.MaxPerModule;

    public Task<StageRunOutcome> RunAsync(CancellationToken cancellationToken) =>
        RunStagesAsync(StageOrder, cancellationToken);

    public Task<StageRunOutcome> RunStageAsync(Stage stage, CancellationToken cancellationToken) =>
        RunStagesAsync([stage], cancellationToken);

    private async Task<StageRunOutcome> RunStagesAsync(
        IReadOnlyList<Stage> stagesToRun,
        CancellationToken cancellationToken)
    {
        // Pre-step: discover the datasource before any stage runs. Cloud-init
        // local-cache parity (restore_from_cache): if a previous run for this
        // still-in-progress instance cached its datasource, restore it instead of
        // re-probing. A module-requested reboot (e.g. SetHostname) must be able to
        // resume even when a network datasource — the OpenStack metadata service —
        // is momentarily unreachable; the data was already fetched on first boot.
        var data = await TryRestoreCachedDataSourceAsync(cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            data = await dataSourceLocator.LocateAsync(cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                logger.LogWarning("No data source available; nothing to provision");
                return StageRunOutcome.NoDataSource.Instance;
            }

            await dataSourceCache.SaveAsync(data, cancellationToken).ConfigureAwait(false);
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
        var buckets = BuildStageBuckets(modules, settings, logger);

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
                    var resolvedUser = await userDataPipeline
                        .ResolveAsync(data.GetUserDataBytes(), cancellationToken)
                        .ConfigureAwait(false);
                    // Vendor-data is a lower-priority user-data source (cloud-init
                    // semantics): resolve it through the same pipeline and merge so
                    // user-data wins on conflict and vendor scripts run first. For
                    // most datasources GetVendorDataBytes() is empty and this is a
                    // no-op; OpenStack supplies it via vendor_data.json.
                    var resolvedVendor = await userDataPipeline
                        .ResolveAsync(data.GetVendorDataBytes(), cancellationToken)
                        .ConfigureAwait(false);
                    resolvedUserData = ResolvedUserData.Combine(resolvedVendor, resolvedUser);
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

                var existingOutcome = await semaphoreStore
                    .ReadOutcomeAsync(moduleKey, frequency, data.InstanceId, cancellationToken)
                    .ConfigureAwait(false);
                if (existingOutcome == OutcomeCompleted)
                {
                    logger.LogInformation(
                        "Skipping module {Module} ({Frequency}); already completed",
                        moduleKey, frequency);
                    continue;
                }
                if (existingOutcome == OutcomeRebootRequested)
                {
                    // Post-reboot resume: the module asked for a reboot last
                    // time; re-enter it so it can finish its work (e.g.
                    // ScriptsUserModule continues with later scripts; modules
                    // like SetHostname idempotently confirm AlreadySet).
                    logger.LogInformation(
                        "Re-entering module {Module} ({Frequency}) after reboot-requested marker",
                        moduleKey, frequency);
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
                        await semaphoreStore.WriteAsync(moduleKey, frequency, data.InstanceId, OutcomeCompleted, cancellationToken)
                            .ConfigureAwait(false);
                        state = state with
                        {
                            CompletedHandlers = AddPerInstanceIfApplicable(state.CompletedHandlers, moduleKey, frequency),
                            // Resume completed: drop the pending marker.
                            PendingHandlers = Without(state.PendingHandlers, moduleKey),
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
                        // Per-module reboot cap. If this module has already
                        // requested a reboot >= MaxRebootsPerModule times,
                        // treat the third+ request as a hard failure to avoid
                        // an unbounded re-entry loop (docs/bugs/0001).
                        var nextRebootCount = state.ModuleRebootCounts.GetValueOrDefault(moduleKey) + 1;
                        if (nextRebootCount > MaxRebootsPerModule)
                        {
                            var capMessage = $"Module {moduleKey} exceeded the reboot cap " +
                                $"({MaxRebootsPerModule} reboots) without making progress.";
                            logger.LogError(capMessage);
                            state = state with
                            {
                                ModuleRebootCounts = WithCount(state.ModuleRebootCounts, moduleKey, nextRebootCount),
                                LastUpdated = DateTimeOffset.UtcNow,
                            };
                            await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                            await reporter.EmitAsync(
                                new ReportingEvent.ModuleFailed(moduleName, capMessage, Exception: null)
                                {
                                    Origin = $"module:{moduleName}",
                                },
                                cancellationToken).ConfigureAwait(false);
                            await reporter.EmitAsync(
                                new ReportingEvent.ProvisioningFailed(capMessage, Exception: null)
                                {
                                    Origin = "stage-runner",
                                },
                                cancellationToken).ConfigureAwait(false);
                            return new StageRunOutcome.Failed(capMessage, Exception: null);
                        }

                        // Write the marker BEFORE returning so a crash between
                        // module.ApplyAsync and the reboot does not leave the
                        // module mid-flight without record of its intent.
                        await semaphoreStore.WriteAsync(moduleKey, frequency, data.InstanceId, OutcomeRebootRequested, cancellationToken)
                            .ConfigureAwait(false);
                        // Reboot-pending must NOT enter CompletedHandlers —
                        // pre-fix that's how state.json reported "all green"
                        // when a script-3 1003 silently dropped scripts 4+.
                        state = state with
                        {
                            PendingHandlers = With(state.PendingHandlers, moduleKey),
                            ModuleRebootCounts = WithCount(state.ModuleRebootCounts, moduleKey, nextRebootCount),
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

    /// <summary>
    /// Restores the cached datasource when resuming a still-in-progress instance,
    /// so a reboot-and-continue does not have to re-reach the (possibly network)
    /// datasource. Returns null — forcing a fresh locate — on the first boot, once
    /// provisioning has fully completed (so a genuinely new instance is detected),
    /// when no reboot-resume is in progress, or when the cache is absent / belongs
    /// to a different instance.
    /// <para>
    /// Deliberate trade-off: when a reboot-resume IS in progress we trust the
    /// cached instance-id and skip the probe, because the network datasource may
    /// be unreachable on the resume boot — that is the whole reason the cache
    /// exists. The consequence is that copying a mid-reboot-resume state
    /// (state.json + datasource.json with RebootCount &gt; 0) onto a *different*
    /// instance would resume as the cached instance instead of re-detecting the
    /// new instance-id. That is out of scope: eryph templates are created from
    /// generalized, shut-down images, never from a VM captured mid-provisioning.
    /// `egs-service reset` clears both files for an intentional re-provision.
    /// </para>
    /// </summary>
    private async Task<DataSourceResult?> TryRestoreCachedDataSourceAsync(CancellationToken cancellationToken)
    {
        var existing = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return null;
        if (existing.CompletedStages.Contains(Stage.Final.ToString()))
            return null;

        // Only short-circuit the probe when a reboot-resume is actually in
        // progress — the one case the cache exists for (a module, e.g.
        // SetHostname, asked for a reboot and we must continue afterwards even if
        // the network datasource is momentarily unreachable). Both markers are set
        // when a reboot is requested. Outside that case (e.g. a clone or rollback
        // mid-provisioning), re-probe so a changed instance-id is still detected by
        // LoadOrResetStateAsync rather than masked by a stale cache.
        var rebootResumeInProgress = existing.RebootCount > 0 || existing.PendingHandlers.Count > 0;
        if (!rebootResumeInProgress)
            return null;

        var cached = await dataSourceCache.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (cached is null || !string.Equals(cached.InstanceId, existing.InstanceId, StringComparison.Ordinal))
            return null;

        logger.LogInformation(
            "Restoring cached datasource for reboot-resume of instance {InstanceId}; skipping re-probe",
            cached.InstanceId);
        return cached;
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
            // Legacy CompletedHandlers entries by definition reached the
            // Completed outcome, so the new outcome-aware gate must treat
            // them as such. Storing a "migrated-..." outcome here would
            // make ReadOutcomeAsync return that string and the gate would
            // re-enter the module on every boot.
            await semaphoreStore.WriteAsync(
                moduleKey,
                ModuleFrequency.PerInstance,
                state.InstanceId,
                OutcomeCompleted,
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

    private static HashSet<string> With(HashSet<string> source, string value) => AddTo(source, value);

    private static HashSet<string> Without(HashSet<string> source, string value)
    {
        var next = new HashSet<string>(source, source.Comparer);
        next.Remove(value);
        return next;
    }

    private static Dictionary<string, int> WithCount(Dictionary<string, int> source, string key, int value)
    {
        var next = new Dictionary<string, int>(source, source.Comparer);
        next[key] = value;
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

    // Reflect over each module once, group by stage, sort by (Order, FullName),
    // and apply the per-stage allowlist / denylist from ProvisioningSettings
    // (RFC 0009). The result is reused for every stage in StageOrder.
    private static Dictionary<Stage, List<StageEntry>> BuildStageBuckets(
        IEnumerable<IModule> modules,
        ProvisioningSettings settings,
        ILogger logger)
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

        var unknownNames = ValidateModuleNames(settings, entries.Select(t => t.Module.GetType()));
        foreach (var (stageName, unknown) in unknownNames)
        {
            logger.LogWarning(
                "RFC 0009: settings reference unknown module(s) {Names} for stage '{Stage}'; ignored.",
                string.Join(", ", unknown), stageName);
        }

        return entries
            .GroupBy(t => t.Attribute!.Stage)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(t => t.Attribute!.Order)
                    .ThenBy(t => t.Module.GetType().FullName, StringComparer.Ordinal)
                    .Where(t => IsModuleEnabled(g.Key, t.Module.GetType(), settings, logger))
                    .Select(t => new StageEntry(t.Module, t.Attribute!.Frequency))
                    .ToList());
    }

    private static bool IsModuleEnabled(
        Stage stage,
        Type moduleType,
        ProvisioningSettings settings,
        ILogger logger)
    {
        var stageSettings = LookupStage(settings, stage);
        if (stageSettings is null)
            return true;

        var shortName = moduleType.Name;

        if (stageSettings.EnabledModules is { Count: > 0 } enabled
            && !enabled.Any(n => NameMatches(n, shortName)))
        {
            logger.LogDebug(
                "RFC 0009: skipping {Module} in stage {Stage} (not in EnabledModules).",
                shortName, stage);
            return false;
        }

        if (stageSettings.DisabledModules is { Count: > 0 } disabled
            && disabled.Any(n => NameMatches(n, shortName)))
        {
            logger.LogDebug(
                "RFC 0009: skipping {Module} in stage {Stage} (in DisabledModules).",
                shortName, stage);
            return false;
        }

        return true;
    }

    private static StageSettings? LookupStage(ProvisioningSettings settings, Stage stage)
    {
        if (settings.Stages is null || settings.Stages.Count == 0)
            return null;

        foreach (var kv in settings.Stages)
        {
            if (string.Equals(kv.Key, stage.ToString(), StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }

    // Case-insensitive match that tolerates the "Module" suffix.
    // "SetHostnameModule" and "SetHostname" both match SetHostnameModule.
    private static bool NameMatches(string configured, string moduleShortName)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return false;

        var trimmed = configured.Trim();
        if (string.Equals(trimmed, moduleShortName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (moduleShortName.EndsWith("Module", StringComparison.Ordinal)
            && string.Equals(
                trimmed,
                moduleShortName[..^"Module".Length],
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // Returns the configured-but-unrecognised names per stage so the caller
    // can log a single Warning per stage instead of one per offending entry.
    private static IEnumerable<(string Stage, IReadOnlyList<string> Unknown)> ValidateModuleNames(
        ProvisioningSettings settings,
        IEnumerable<Type> discoveredModules)
    {
        if (settings.Stages is null || settings.Stages.Count == 0)
            yield break;

        var shortNames = discoveredModules
            .Select(t => t.Name)
            .ToList();

        foreach (var kv in settings.Stages)
        {
            var configured = (kv.Value.EnabledModules ?? Enumerable.Empty<string>())
                .Concat(kv.Value.DisabledModules ?? Enumerable.Empty<string>())
                .Select(n => n?.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var unknown = configured
                .Where(n => !shortNames.Any(s => NameMatches(n, s)))
                .ToList();

            if (unknown.Count > 0)
                yield return (kv.Key, unknown);
        }
    }

    private readonly record struct StageEntry(IModule Module, ModuleFrequency Frequency);
}
