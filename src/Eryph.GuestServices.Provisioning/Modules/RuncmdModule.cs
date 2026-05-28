using System.Security.Cryptography;
using System.Text;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using RuncmdEntryModel = Eryph.GuestServices.CloudConfig.RuncmdEntry;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Runs <c>runcmd</c> entries with cbi exit-code semantics (1001 = reboot
/// then move on; 1003 = reboot and re-run the same entry). Per-entry
/// checkpoint state survives reboots so multi-stage installers
/// (driver → reboot → role → reboot → done) make incremental progress.
/// </summary>
[Stage(Stage.Config, Order = 4, Frequency = ModuleFrequency.PerInstance)]
internal sealed class RuncmdModule(
    ILogger<RuncmdModule> logger,
    ProvisioningSettings settings,
    IRuncmdCheckpointStore checkpointStore) : IModule
{
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
            var hash = ComputeContentHash(entry);

            if (checkpoint.IsCompleted(ordinal, hash))
            {
                logger.LogInformation(
                    "Skipping runcmd #{Index} — already completed in a prior run (resume).",
                    ordinal);
                continue;
            }

            var progressKey = UserCodeCheckpoint.ProgressKey(ordinal, hash);
            var progress = checkpoint.Progress.GetValueOrDefault(progressKey) ?? new EntryProgress();
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
                logger.LogError(ex, "runcmd entry #{Index} failed to start.", ordinal);
                checkpoint = RebootExitDispatcher.Apply(
                    checkpoint, progressKey, ordinal, hash,
                    new RebootExitDispatcher.Result(
                        RebootExitDispatcher.Action.Continue,
                        MarkCompleted: true,
                        NextProgress: progress,
                        Message: $"runcmd #{ordinal} failed to launch."));
                await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                continue;
            }

            LogResult(ordinal, entry, result);

            var dispatch = RebootExitDispatcher.Dispatch(
                result.ExitCode, result.StdOut, progress, settings.Reboot,
                ordinal, "runcmd", logger);

            checkpoint = RebootExitDispatcher.Apply(checkpoint, progressKey, ordinal, hash, dispatch);
            await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);

            switch (dispatch.Outcome)
            {
                case RebootExitDispatcher.Action.Continue:
                    continue;
                case RebootExitDispatcher.Action.Reboot:
                    logger.LogInformation(
                        "runcmd #{Index} → reboot ({Message}).", ordinal, dispatch.Message);
                    return ModuleOutcome.RebootForUserScript(dispatch.Message);
                case RebootExitDispatcher.Action.Fail:
                    logger.LogError(dispatch.Message);
                    return ModuleOutcome.Fail(dispatch.Message);
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

    // Stable identity hash over the entry's command content. Hashing the
    // shell command or the argv form joined with a separator that cannot
    // collide with either (NUL). Identity key only — never compared
    // cryptographically.
    private static string ComputeContentHash(RuncmdEntryModel entry)
    {
        var sb = new StringBuilder();
        if (entry.IsShellCommand)
        {
            sb.Append("shell\0");
            sb.Append(entry.Command ?? string.Empty);
        }
        else
        {
            sb.Append("argv\0");
            var argv = entry.Argv ?? [];
            sb.Append(argv.Count);
            foreach (var part in argv)
            {
                sb.Append('\0');
                sb.Append(part ?? string.Empty);
            }
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
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

    // cbi exit-code constants — see plugins/common/execcmd.py::get_plugin_return_value
    public const int RebootAndDoneExitCode = 1001;
    public const int RerunOnNextBootExitCode = 1002;
    public const int RebootAndContinueExitCode = 1003;
}
