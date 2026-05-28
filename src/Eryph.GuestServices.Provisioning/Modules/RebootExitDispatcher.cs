using Eryph.GuestServices.Provisioning.Configuration;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Translates the cbi exit-code contract (1001 / 1002 / 1003 plus 0 / other)
/// into a state transition for a single user-code item. Used by both
/// <see cref="RuncmdModule"/> and <see cref="ScriptsUserModule"/>; the
/// semantics are identical — 1001 marks the item completed, 1003 leaves it
/// in-flight so it runs again on resume.
/// </summary>
internal static class RebootExitDispatcher
{
    public enum Action { Continue, Reboot, Fail }

    public sealed record Result(
        Action Outcome,
        bool MarkCompleted,
        EntryProgress NextProgress,
        string Message);

    /// <summary>
    /// Folds the dispatch result into a checkpoint: either appends the item
    /// to <see cref="UserCodeCheckpoint.Completed"/> (and drops its progress
    /// record), or updates its progress entry in place. Returns the new
    /// checkpoint instance — the caller persists it.
    /// </summary>
    public static UserCodeCheckpoint Apply(
        UserCodeCheckpoint checkpoint,
        string progressKey,
        int ordinal,
        string hash,
        Result dispatch)
    {
        if (dispatch.MarkCompleted)
        {
            var completed = new List<CheckpointEntry>(checkpoint.Completed)
            {
                new(ordinal, hash),
            };
            var progress = new Dictionary<string, EntryProgress>(checkpoint.Progress, StringComparer.Ordinal);
            progress.Remove(progressKey);
            return checkpoint with { Completed = completed, Progress = progress };
        }

        var nextProgress = new Dictionary<string, EntryProgress>(checkpoint.Progress, StringComparer.Ordinal)
        {
            [progressKey] = dispatch.NextProgress,
        };
        return checkpoint with { Progress = nextProgress };
    }

    public static Result Dispatch(
        int exitCode,
        string stdout,
        EntryProgress current,
        RebootSettings settings,
        int ordinal,
        string label,
        ILogger logger)
    {
        switch (exitCode)
        {
            case 0:
                return new Result(
                    Action.Continue,
                    MarkCompleted: true,
                    NextProgress: current,
                    Message: $"{label} #{ordinal} succeeded.");

            case 1001:
                // Reboot, item is done. The progress record is dropped on
                // MarkCompleted so RebootAttempts is moot; any directive on
                // this run is also moot since the item will not re-enter.
                return new Result(
                    Action.Reboot,
                    MarkCompleted: true,
                    NextProgress: current,
                    Message: $"{label} #{ordinal} requested reboot (exit 1001, item done).");

            case 1002:
                // cbi's "re-execute plugin on next boot without rebooting" has
                // no eryph equivalent (our PerInstance gate closes the module
                // on Completed). Treat as a non-zero error and move on.
                logger.LogWarning(
                    "{Label} #{Index} returned 1002 (re-run on next boot) — not supported on eryph; treating as error and continuing.",
                    label, ordinal);
                return new Result(
                    Action.Continue,
                    MarkCompleted: true,
                    NextProgress: current,
                    Message: $"{label} #{ordinal} returned 1002 (unsupported).");

            case 1003:
            {
                var emittedRaise = RebootDirective.ParseRaise(
                    stdout,
                    current.OverrideLimit ?? settings.MaxPerScript,
                    settings.AllowScriptOverride,
                    ordinal, label, logger);
                var resolvedLimit = emittedRaise ?? current.OverrideLimit ?? settings.MaxPerScript;
                var nextAttempts = current.RebootAttempts + 1;
                if (nextAttempts > resolvedLimit)
                {
                    return new Result(
                        Action.Fail,
                        MarkCompleted: false,
                        NextProgress: current,
                        Message: $"{label} #{ordinal} exceeded per-script reboot limit ({nextAttempts}/{resolvedLimit}).");
                }

                // 1003 means "re-run me on resume" (cbi parity: the plugin
                // re-executes next boot, re-iterating all items including
                // this one). Do NOT mark completed. Persist the new override
                // only when the script actually emitted one — otherwise keep
                // the prior value so a later setting change to MaxPerScript
                // takes effect.
                var nextProgress = current with
                {
                    RebootAttempts = nextAttempts,
                    OverrideLimit = emittedRaise ?? current.OverrideLimit,
                };
                return new Result(
                    Action.Reboot,
                    MarkCompleted: false,
                    NextProgress: nextProgress,
                    Message: $"{label} #{ordinal} requested reboot (exit 1003, will re-run on resume).");
            }

            default:
                // Any other non-zero exit: log and carry on so a later item
                // still gets a chance. The item is marked completed because a
                // resume would re-run the same failure.
                logger.LogError(
                    "{Label} #{Index} exited with code {Code}; continuing.",
                    label, ordinal, exitCode);
                return new Result(
                    Action.Continue,
                    MarkCompleted: true,
                    NextProgress: current,
                    Message: $"{label} #{ordinal} exited with code {exitCode}.");
        }
    }
}
