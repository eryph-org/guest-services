using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using RuncmdEntryModel = Eryph.GuestServices.CloudConfig.RuncmdEntry;

namespace Eryph.GuestServices.Provisioning.Modules;

[Stage(Stage.Config, Order = 4, Frequency = ModuleFrequency.PerInstance)]
internal sealed class RuncmdModule(ILogger<RuncmdModule> logger) : IModule
{
    /// <summary>
    /// Exit code reserved by the cloudbase-init "reboot and continue"
    /// convention. eryph guests follow the same contract.
    /// </summary>
    public const int RebootRequestedExitCode = 1003;

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;
        if (config.Runcmd is null || config.Runcmd.Count == 0)
            return ModuleOutcome.Ok();

        for (var i = 0; i < config.Runcmd.Count; i++)
        {
            var entry = config.Runcmd[i];
            RunCommandResult result;

            try
            {
                result = await ExecuteAsync(entry, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "runcmd entry #{Index} failed to start.", i + 1);
                continue;
            }

            LogResult(i + 1, entry, result);

            if (result.ExitCode == RebootRequestedExitCode)
            {
                logger.LogInformation(
                    "runcmd entry #{Index} returned {Code}; requesting reboot-and-continue.",
                    i + 1, RebootRequestedExitCode);
                return ModuleOutcome.Reboot($"runcmd entry #{i + 1} requested reboot (exit {RebootRequestedExitCode}).");
            }

            if (result.ExitCode != 0)
                logger.LogError(
                    "runcmd entry #{Index} exited with code {Code}; continuing with remaining commands.",
                    i + 1, result.ExitCode);
        }

        return ModuleOutcome.Ok();
    }

    private static async Task<RunCommandResult> ExecuteAsync(
        RuncmdEntryModel entry,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (entry.IsShellCommand)
        {
            if (string.IsNullOrWhiteSpace(entry.Command))
                throw new InvalidOperationException("runcmd shell entry has empty Command.");
            return await context.Os.RunShellCommandAsync(entry.Command, cancellationToken).ConfigureAwait(false);
        }

        if (entry.Argv is null || entry.Argv.Count == 0)
            throw new InvalidOperationException("runcmd argv entry has empty Argv.");

        return await context.Os.RunArgvCommandAsync(entry.Argv, cancellationToken).ConfigureAwait(false);
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
