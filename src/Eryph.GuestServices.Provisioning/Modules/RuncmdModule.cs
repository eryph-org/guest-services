using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using RuncmdEntryModel = Eryph.GuestServices.CloudConfig.RuncmdEntry;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Runs <c>runcmd</c> entries with full cloudbase-init exit-code semantics
/// (1001 = reboot then move on; 1003 = reboot and re-enter the same entry).
/// Per-entry checkpoint state survives reboots so multi-stage installers
/// (driver → reboot → role → reboot → done) make incremental progress.
/// </summary>
[Stage(Stage.Config, Order = 4, Frequency = ModuleFrequency.PerInstance)]
internal sealed class RuncmdModule(
    ILogger<RuncmdModule> logger,
    ProvisioningSettings settings,
    IRuncmdCheckpointStore checkpointStore) : IModule
{
    // cbi exit-code contract — see plugins/common/execcmd.py::get_plugin_return_value
    // The 1001..1003 range is a bitmask: bit 0 = reboot, bit 1 = re-execute.
    public const int RebootAndDoneExitCode = 1001;          // reboot now, entry is done
    public const int RerunOnNextBootExitCode = 1002;        // no reboot, re-run on next boot (NOT supported)
    public const int RebootAndContinueExitCode = 1003;      // reboot now, re-run same entry

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;
        if (config.Runcmd is null || config.Runcmd.Count == 0)
            return ModuleOutcome.Ok();

        var instanceId = context.DataSource.InstanceId;
        var checkpoint = await checkpointStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < config.Runcmd.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = config.Runcmd[i];
            var ordinal = i + 1;
            var contentHash = RuncmdCheckpoint.ComputeContentHash(
                entry.IsShellCommand ? entry.Command : null,
                entry.IsShellCommand ? null : entry.Argv);

            if (checkpoint.IsCompleted(ordinal, contentHash))
            {
                logger.LogInformation(
                    "Skipping runcmd #{Index} — already completed in a prior run (resume).",
                    ordinal);
                continue;
            }

            var progressKey = RuncmdCheckpoint.ProgressKey(ordinal, contentHash);
            var progress = checkpoint.Progress.GetValueOrDefault(progressKey) ?? new RuncmdEntryProgress();
            var effectiveLimit = progress.OverrideLimit ?? settings.Reboot.MaxPerScript;

            var envVars = RebootEnvVars.Build(ordinal, progress.RebootAttempts, effectiveLimit);

            RunCommandResult result;
            try
            {
                result = await ExecuteAsync(entry, envVars, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Cannot launch the child at all — out of scope for the reboot
                // contract. Log + carry on so a later entry still gets a chance.
                logger.LogError(ex, "runcmd entry #{Index} failed to start.", ordinal);
                checkpoint = MarkCompleted(checkpoint, progressKey, ordinal, contentHash);
                await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                continue;
            }

            LogResult(ordinal, entry, result);

            switch (result.ExitCode)
            {
                case RebootAndDoneExitCode:
                {
                    // 1001 marks the entry done — the per-entry quota cannot
                    // accumulate against an entry that won't re-enter, so we
                    // don't consult it here (would be dead branch). Any
                    // script-supplied limit directive on a 1001 line is
                    // ignored: the entry is completing anyway.
                    var nextAttempts = progress.RebootAttempts + 1;
                    checkpoint = MarkCompleted(checkpoint, progressKey, ordinal, contentHash);
                    await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "runcmd #{Index} returned 1001 (reboot, entry done); attempt {Attempt}.",
                        ordinal, nextAttempts);
                    return ModuleOutcome.RebootForUserScript(
                        $"runcmd entry #{ordinal} requested reboot (exit 1001, entry done).");
                }

                case RebootAndContinueExitCode:
                {
                    var emittedOverride = RebootDirective.ParseRaise(
                        result.StdOut, effectiveLimit, settings.Reboot.AllowScriptOverride,
                        ordinal, "runcmd", logger);
                    var newLimit = emittedOverride ?? effectiveLimit;
                    var nextAttempts = progress.RebootAttempts + 1;
                    if (nextAttempts > newLimit)
                        return FailEntry(ordinal, nextAttempts, newLimit);
                    var updatedProgress = progress with
                    {
                        RebootAttempts = nextAttempts,
                        // Only PERSIST a new override when the script actually emitted
                        // a directive on THIS run; otherwise keep whatever was already
                        // stored (null = "no override, use configured default"). Writing
                        // the resolved value unconditionally would pin the entry to a
                        // snapshot of MaxRebootsPerEntry and break later config edits.
                        OverrideLimit = emittedOverride ?? progress.OverrideLimit,
                    };
                    checkpoint = checkpoint with
                    {
                        Progress = WithProgress(checkpoint.Progress, progressKey, updatedProgress),
                    };
                    await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "runcmd #{Index} returned 1003 (reboot and continue); attempt {Attempt}/{Limit}.",
                        ordinal, nextAttempts, newLimit);
                    return ModuleOutcome.RebootForUserScript(
                        $"runcmd entry #{ordinal} requested reboot (exit 1003, will re-enter).");
                }

                case RerunOnNextBootExitCode:
                    // cbi's 1002 ("re-execute on next boot without rebooting") has
                    // no eryph equivalent: our PerInstance gating closes the module
                    // after Completed. Treat as a non-zero error and move on.
                    logger.LogWarning(
                        "runcmd #{Index} returned 1002 (re-run on next boot) — not supported on eryph; treating as error and continuing.",
                        ordinal);
                    checkpoint = MarkCompleted(checkpoint, progressKey, ordinal, contentHash);
                    await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                    continue;

                case 0:
                    checkpoint = MarkCompleted(checkpoint, progressKey, ordinal, contentHash);
                    await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                    continue;

                default:
                    logger.LogError(
                        "runcmd #{Index} exited with code {Code}; continuing with remaining commands.",
                        ordinal, result.ExitCode);
                    checkpoint = MarkCompleted(checkpoint, progressKey, ordinal, contentHash);
                    await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                    continue;
            }
        }

        return ModuleOutcome.Ok();
    }

    private static async Task<RunCommandResult> ExecuteAsync(
        RuncmdEntryModel entry,
        IReadOnlyDictionary<string, string> environment,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (entry.IsShellCommand)
        {
            if (string.IsNullOrWhiteSpace(entry.Command))
                throw new InvalidOperationException("runcmd shell entry has empty Command.");
            return await context.Os.RunShellCommandAsync(entry.Command, environment, cancellationToken).ConfigureAwait(false);
        }

        if (entry.Argv is null || entry.Argv.Count == 0)
            throw new InvalidOperationException("runcmd argv entry has empty Argv.");

        return await context.Os.RunArgvCommandAsync(entry.Argv, environment, cancellationToken).ConfigureAwait(false);
    }


    private static RuncmdCheckpoint MarkCompleted(
        RuncmdCheckpoint checkpoint,
        string progressKey,
        int ordinal,
        string contentHash)
    {
        var completed = new List<RuncmdCheckpointEntry>(checkpoint.Completed)
        {
            new(ordinal, contentHash),
        };
        var progress = new Dictionary<string, RuncmdEntryProgress>(checkpoint.Progress, StringComparer.Ordinal);
        progress.Remove(progressKey);
        return checkpoint with
        {
            Completed = completed,
            Progress = progress,
        };
    }

    private static Dictionary<string, RuncmdEntryProgress> WithProgress(
        Dictionary<string, RuncmdEntryProgress> source,
        string key,
        RuncmdEntryProgress value)
    {
        var next = new Dictionary<string, RuncmdEntryProgress>(source, StringComparer.Ordinal)
        {
            [key] = value,
        };
        return next;
    }

    private ModuleOutcome FailEntry(int ordinal, int attempts, int limit)
    {
        var message = $"runcmd entry #{ordinal} exceeded per-entry reboot limit ({attempts}/{limit}).";
        logger.LogError(message);
        return ModuleOutcome.Fail(message);
    }

    private void LogResult(int index, RuncmdEntryModel entry, RunCommandResult result)
    {
        var description = entry.IsShellCommand
            ? entry.Command ?? "<empty>"
            : string.Join(' ', entry.Argv ?? []);

        logger.LogInformation(
            "runcmd #{Index} '{Command}' exited with {Code}.",
            index, description, result.ExitCode);

        if (!string.IsNullOrWhiteSpace(result.StdOut))
            logger.LogDebug("runcmd #{Index} stdout:\n{StdOut}", index, result.StdOut);
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            logger.LogDebug("runcmd #{Index} stderr:\n{StdErr}", index, result.StdErr);
    }
}
