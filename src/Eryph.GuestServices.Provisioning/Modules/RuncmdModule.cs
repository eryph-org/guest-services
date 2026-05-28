using System.Text.RegularExpressions;
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
internal sealed partial class RuncmdModule(
    ILogger<RuncmdModule> logger,
    ProvisioningSettings settings,
    IRuncmdCheckpointStore checkpointStore) : IModule
{
    // cbi exit-code contract — see plugins/common/execcmd.py::get_plugin_return_value
    // The 1001..1003 range is a bitmask: bit 0 = reboot, bit 1 = re-execute.
    public const int RebootAndDoneExitCode = 1001;          // reboot now, entry is done
    public const int RerunOnNextBootExitCode = 1002;        // no reboot, re-run on next boot (NOT supported)
    public const int RebootAndContinueExitCode = 1003;      // reboot now, re-run same entry

    // Marker the script can emit on stdout to raise its own per-entry limit.
    // Uses a ## directive prefix (similar to Azure DevOps task commands) so a
    // script that dumps its environment cannot accidentally trip this regex —
    // the injected EGS_RUNCMD_REBOOT_LIMIT=N env var line would otherwise look
    // identical to a directive. Last occurrence wins. Only RAISES the limit;
    // a lower emitted value is logged and ignored. Persisted in the entry's
    // checkpoint so the override survives reboots without re-emission.
    private const string LimitDirectivePrefix = "##egs.runcmd.reboot_limit";

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
            var effectiveLimit = progress.OverrideLimit ?? settings.Runcmd.MaxRebootsPerEntry;

            var envVars = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EGS_RUNCMD_ENTRY_INDEX"] = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EGS_RUNCMD_REBOOT_COUNT"] = progress.RebootAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EGS_RUNCMD_REBOOT_LIMIT"] = effectiveLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

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
                    var emittedOverride = ParseEmittedLimit(result.StdOut, ordinal, progress, settings);
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

    // Returns the script-supplied new per-entry limit, or null when the script
    // didn't emit a usable directive on this run. Only RAISES are honoured —
    // a directive lower than the current effective limit is logged and ignored
    // to keep the override semantics monotonic (matches the "bump your limit"
    // contract; prevents a single bad output line from killing the current
    // attempt). Only stdout is inspected: the doc'd interface is a stdout
    // directive, and admitting stderr would let unrelated error text trip the
    // parser.
    private int? ParseEmittedLimit(
        string stdout,
        int ordinal,
        RuncmdEntryProgress progress,
        ProvisioningSettings settings)
    {
        if (!settings.Runcmd.AllowScriptOverride)
            return null;
        var emitted = ExtractDirective(stdout);
        if (emitted is null)
            return null;
        var currentLimit = progress.OverrideLimit ?? settings.Runcmd.MaxRebootsPerEntry;
        if (emitted <= currentLimit)
        {
            if (emitted < currentLimit)
                logger.LogWarning(
                    "runcmd #{Index} emitted {Directive}={Emitted} but the directive only raises the limit (current {Current}); ignoring.",
                    ordinal, LimitDirectivePrefix, emitted, currentLimit);
            return null;
        }
        logger.LogInformation(
            "runcmd #{Index} raised its per-entry reboot limit to {NewLimit} via {Directive} directive.",
            ordinal, emitted, LimitDirectivePrefix);
        return emitted;
    }

    private static int? ExtractDirective(string output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        int? last = null;
        foreach (Match match in LimitDirective().Matches(output))
        {
            if (int.TryParse(
                    match.Groups[1].Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value)
                && value > 0)
            {
                last = value;
            }
        }
        return last;
    }

    // [0-9]+ (not \d+) to match ASCII digits only — .NET's \d also matches
    // Unicode digit categories which int.TryParse with InvariantCulture would
    // then reject anyway. The "##" prefix disambiguates the directive from a
    // shell env-var assignment, so a script that prints its environment cannot
    // accidentally raise its own limit.
    [GeneratedRegex(@"^\s*##egs\.runcmd\.reboot_limit\s*=\s*([0-9]+)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LimitDirective();

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
