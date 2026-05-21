using System.Text;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Cloud-init's <c>cc_scripts_user</c> equivalent. Runs in the Final stage,
/// executes every <see cref="ScriptPayload"/> collected by the user-data
/// pipeline. Two-phase pattern: Config-stage modules stage files / config;
/// this Final-stage module runs the user-supplied scripts after everything
/// else is settled.
/// </summary>
[Stage(Stage.Final, Order = 0)]
internal sealed class ScriptsUserModule(
    ILogger<ScriptsUserModule> logger,
    ProvisioningSettings settings) : IModule
{
    /// <summary>
    /// Exit code reserved by the cloudbase-init "reboot and continue"
    /// convention. Same value the RuncmdModule honors.
    /// </summary>
    public const int RebootRequestedExitCode = 1003;

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (userData.Scripts.Count == 0)
        {
            logger.LogDebug("No user-data scripts to run.");
            return ModuleOutcome.Ok();
        }

        var scriptDirectory = Environment.ExpandEnvironmentVariables(settings.Scripts.PerInstanceDirectory);
        await context.Os.EnsureDirectoryAsync(scriptDirectory, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < userData.Scripts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var script = userData.Scripts[index];
            var scriptPath = StagePath(scriptDirectory, index, script);

            try
            {
                await context.Os.WriteFileAsync(scriptPath, script.Body, append: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to stage script #{Index} to '{Path}'; skipping.", index + 1, scriptPath);
                continue;
            }

            RunCommandResult result;
            try
            {
                result = await ExecuteAsync(scriptPath, script.Kind, context, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Script #{Index} ({Path}) has unsupported kind {Kind}; skipping.",
                    index + 1, scriptPath, script.Kind);
                continue;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Script #{Index} ({Path}) failed to start.", index + 1, scriptPath);
                continue;
            }

            LogResult(index + 1, scriptPath, result);

            if (result.ExitCode == RebootRequestedExitCode)
            {
                logger.LogInformation(
                    "Script #{Index} ({Path}) requested reboot-and-continue (exit {Code}).",
                    index + 1, scriptPath, RebootRequestedExitCode);
                return ModuleOutcome.Reboot($"user-data script '{scriptPath}' requested reboot (exit {RebootRequestedExitCode}).");
            }

            if (result.ExitCode != 0)
                logger.LogError(
                    "Script #{Index} ({Path}) exited with code {Code}; continuing with remaining scripts.",
                    index + 1, scriptPath, result.ExitCode);
        }

        return ModuleOutcome.Ok();
    }

    private static string StagePath(string directory, int index, ScriptPayload script)
    {
        var extension = script.Kind switch
        {
            ScriptKind.PowerShell => ".ps1",
            ScriptKind.Cmd => ".cmd",
            ScriptKind.ShellScript => ".sh",
            _ => ".txt",
        };
        var baseName = SanitizeName(script.Filename) ?? $"script-{index + 1}";
        return Path.Combine(directory, $"{index + 1:000}-{baseName}{extension}");
    }

    private static string? SanitizeName(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(filename.Length);
        foreach (var c in filename)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var name = Path.GetFileNameWithoutExtension(sb.ToString());
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private async Task<RunCommandResult> ExecuteAsync(
        string scriptPath,
        ScriptKind kind,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        return kind switch
        {
            ScriptKind.PowerShell => await context.Os.RunArgvCommandAsync(
                ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                cancellationToken).ConfigureAwait(false),

            ScriptKind.Cmd => await context.Os.RunArgvCommandAsync(
                ["cmd.exe", "/c", scriptPath],
                cancellationToken).ConfigureAwait(false),

            // Windows guests may not have a POSIX shell. We attempt cmd.exe but the
            // script body is unlikely to be sh-compatible. The user-data pipeline
            // should normally classify Windows scripts as PowerShell or Cmd; a
            // ShellScript here means the shebang was #!/bin/sh which is alien.
            ScriptKind.ShellScript => await context.Os.RunArgvCommandAsync(
                ["cmd.exe", "/c", scriptPath],
                cancellationToken).ConfigureAwait(false),

            _ => throw new NotSupportedException($"Script kind {kind} cannot be executed on Windows."),
        };
    }

    private void LogResult(int index, string scriptPath, RunCommandResult result)
    {
        logger.LogInformation("Script #{Index} ({Path}) exited with {Code}.", index, scriptPath, result.ExitCode);
        if (!string.IsNullOrWhiteSpace(result.StdOut))
            logger.LogDebug("Script #{Index} stdout:\n{StdOut}", index, result.StdOut);
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            logger.LogDebug("Script #{Index} stderr:\n{StdErr}", index, result.StdErr);
    }
}
