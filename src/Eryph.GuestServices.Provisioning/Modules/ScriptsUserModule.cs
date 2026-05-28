using System.Text;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
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
[Stage(Stage.Final, Order = 0, Frequency = ModuleFrequency.PerInstance)]
internal sealed class ScriptsUserModule(
    ILogger<ScriptsUserModule> logger,
    ProvisioningSettings settings,
    IReportingDispatcher reporter,
    IScriptCheckpointStore checkpointStore) : IModule
{
    // cbi exit-code contract — see plugins/common/execcmd.py::get_plugin_return_value
    // The 1001..1003 range is a bitmask: bit 0 = reboot, bit 1 = re-execute plugin.
    //   1001: reboot, plugin done (this script won't run again)
    //   1002: re-run plugin on next boot, no reboot — NOT supported on eryph
    //   1003: reboot, re-execute plugin (the script that emitted 1003 runs again)
    // Eryph's per-script checkpoint encodes this: 1001 marks the script
    // completed; 1003 leaves it in-flight so the resume re-runs it.
    public const int RebootAndDoneExitCode = 1001;
    public const int RerunOnNextBootExitCode = 1002;
    public const int RebootRequestedExitCode = 1003;

    /// <summary>
    /// Directory where per-script stdout/stderr logs are written. One log
    /// file per script, named after the staged script (so an operator can
    /// trivially correlate <c>001-enable_rd.ps1</c> in the scripts dir with
    /// <c>001-enable_rd.ps1.log</c> in the logs dir).
    /// </summary>
    private const string LogsDirectory = @"%ProgramData%\eryph\provisioning\logs";

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

        var scriptDirectory = context.Os.ExpandEnvironmentVariables(settings.Scripts.PerInstanceDirectory);
        var logDirectory = context.Os.ExpandEnvironmentVariables(LogsDirectory);
        await context.Os.EnsureDirectoryAsync(scriptDirectory, cancellationToken).ConfigureAwait(false);
        await context.Os.EnsureDirectoryAsync(logDirectory, cancellationToken).ConfigureAwait(false);

        var instanceId = context.DataSource.InstanceId;
        var checkpoint = await checkpointStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < userData.Scripts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var script = userData.Scripts[index];
            var ordinal = index + 1;

            // ScriptKind.Other parts were classified by ScriptKindDetector as
            // unrunnable on Windows (e.g. .sh extension, POSIX shebang, or
            // genuinely unrecognised). The detector already logged a warning
            // at classification time; the module just skips them.
            if (script.Kind == ScriptKind.Other)
            {
                logger.LogInformation(
                    "Skipping script #{Index} ('{Filename}') — kind Other (see earlier warning).",
                    ordinal,
                    script.Filename ?? "<root>");
                continue;
            }

            var bodyHash = ComputeBodyHash(script.Body);

            if (checkpoint.IsCompleted(ordinal, bodyHash))
            {
                logger.LogInformation(
                    "Skipping script #{Index} ('{Filename}') — already completed in a prior run (resume).",
                    ordinal, script.Filename ?? "<root>");
                continue;
            }

            var scriptName = BuildScriptName(ordinal, script);
            var scriptPath = WindowsPath.Combine(scriptDirectory, scriptName);
            var progressKey = UserCodeCheckpoint.ProgressKey(ordinal, bodyHash);
            var progress = checkpoint.Progress.GetValueOrDefault(progressKey) ?? new EntryProgress();

            try
            {
                await context.Os.WriteFileAsync(scriptPath, script.Body, append: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Mark as completed + persist so a transient or persistent
                // staging failure does not silently re-stage on every resume.
                // (Pre-fix this `continue`d without saving — the script would
                // be retried on every boot until the underlying problem was
                // fixed externally.)
                logger.LogError(ex, "Failed to stage script #{Index} to '{Path}'; skipping.", ordinal, scriptPath);
                checkpoint = RebootExitDispatcher.Apply(
                    checkpoint, progressKey, ordinal, bodyHash,
                    new RebootExitDispatcher.Result(
                        RebootExitDispatcher.Action.Continue,
                        MarkCompleted: true,
                        NextProgress: progress,
                        Message: $"script #{ordinal} failed to stage."));
                await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var effectiveLimit = progress.OverrideLimit ?? settings.Reboot.MaxPerScript;
            var envVars = RebootEnvVars.Build(ordinal, progress.RebootAttempts, effectiveLimit);

            RunCommandResult result;
            try
            {
                result = await ExecuteAsync(scriptPath, script.Kind, envVars, context, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                // Unsupported kind is a static property of the payload (not a
                // transient failure), so persist completion so we don't try
                // again next boot.
                logger.LogWarning(ex, "Script #{Index} ({Path}) has unsupported kind {Kind}; skipping.",
                    ordinal, scriptPath, script.Kind);
                checkpoint = RebootExitDispatcher.Apply(
                    checkpoint, progressKey, ordinal, bodyHash,
                    new RebootExitDispatcher.Result(
                        RebootExitDispatcher.Action.Continue,
                        MarkCompleted: true,
                        NextProgress: progress,
                        Message: $"script #{ordinal} unsupported kind."));
                await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                // Same reasoning as the staging-failure branch: persist
                // completion so a launch failure doesn't infinite-retry.
                logger.LogError(ex, "Script #{Index} ({Path}) failed to start.", ordinal, scriptPath);
                checkpoint = RebootExitDispatcher.Apply(
                    checkpoint, progressKey, ordinal, bodyHash,
                    new RebootExitDispatcher.Result(
                        RebootExitDispatcher.Action.Continue,
                        MarkCompleted: true,
                        NextProgress: progress,
                        Message: $"script #{ordinal} failed to launch."));
                await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await WriteLogAsync(context.Os, logDirectory, scriptName, scriptPath, result, cancellationToken)
                .ConfigureAwait(false);
            LogResult(ordinal, scriptPath, result);
            await ReportProgressAsync(scriptName, result, cancellationToken).ConfigureAwait(false);

            var dispatch = RebootExitDispatcher.Dispatch(
                result.ExitCode, result.StdOut, progress, settings.Reboot,
                ordinal, "script", logger);

            checkpoint = RebootExitDispatcher.Apply(checkpoint, progressKey, ordinal, bodyHash, dispatch);
            await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);

            switch (dispatch.Outcome)
            {
                case RebootExitDispatcher.Action.Continue:
                    continue;
                case RebootExitDispatcher.Action.Reboot:
                    logger.LogInformation(
                        "Script #{Index} → reboot ({Message}).", ordinal, dispatch.Message);
                    return ModuleOutcome.RebootForUserScript(dispatch.Message);
                case RebootExitDispatcher.Action.Fail:
                    logger.LogError(dispatch.Message);
                    return ModuleOutcome.Fail(dispatch.Message);
            }
        }

        return ModuleOutcome.Ok();
    }

    // Identity hash over the script's body bytes. Operator edits to the
    // body invalidate the hash so the checkpoint cannot mistakenly skip a
    // replaced script.
    private static string ComputeBodyHash(byte[] body) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));

    // Preserves the operator-authored filename (extension included) prefixed
    // with the declaration order. Eryph genes ship meaningful filenames like
    // "enable_rd.ps1" — keep them so guest-side logs match what was written.
    private static string BuildScriptName(int ordinal, ScriptPayload script)
    {
        var sanitized = SanitizeName(script.Filename);
        if (sanitized is not null)
            return $"{ordinal:000}-{sanitized}";

        // No filename — generate one from the inferred kind so we can still
        // place it on disk with an appropriate extension. The detector has
        // already filtered out ScriptKind.Other before we get here.
        var extension = script.Kind switch
        {
            ScriptKind.PowerShell => ".ps1",
            ScriptKind.Cmd => ".cmd",
            ScriptKind.ShellScript => ".sh",
            _ => ".txt",
        };
        return $"{ordinal:000}-script{extension}";
    }

    private static string? SanitizeName(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(filename.Length);
        foreach (var c in filename)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var name = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static async Task<RunCommandResult> ExecuteAsync(
        string scriptPath,
        ScriptKind kind,
        IReadOnlyDictionary<string, string> environment,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        return kind switch
        {
            // We wrap the .ps1 in `-Command "& { ... ; & '<path>' ; exit $LASTEXITCODE }"`
            // so we can force UTF-8 on PowerShell's output streams BEFORE the
            // user script runs. Without this, PowerShell 5.1 emits in the OEM
            // code page and the .NET-side StandardOutputEncoding=UTF-8 decoder
            // mangles any non-ASCII characters the script writes (which then
            // end up in the per-script .log file).
            ScriptKind.PowerShell => await context.Os.RunArgvCommandAsync(
                [
                    "powershell.exe", "-NoProfile", "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-Command", PowerShellScriptWrapper.BuildScriptWrapper(scriptPath),
                ],
                environment,
                cancellationToken).ConfigureAwait(false),

            ScriptKind.Cmd => await context.Os.RunArgvCommandAsync(
                ["cmd.exe", "/c", scriptPath],
                environment,
                cancellationToken).ConfigureAwait(false),

            // ScriptKind.ShellScript should never reach this point on Windows
            // because ScriptKindDetector resolves POSIX scripts to
            // ScriptKind.Other. The branch is retained defensively for
            // non-Windows test runs / future cross-platform support.
            _ => throw new NotSupportedException($"Script kind {kind} cannot be executed on Windows."),
        };
    }

    private async Task WriteLogAsync(
        IWindowsOs os,
        string logDirectory,
        string scriptName,
        string scriptPath,
        RunCommandResult result,
        CancellationToken cancellationToken)
    {
        var logPath = WindowsPath.Combine(logDirectory, scriptName + ".log");
        var sb = new StringBuilder();
        sb.Append("script: ").AppendLine(scriptPath);
        sb.Append("exit-code: ").AppendLine(result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("---- stdout ----");
        sb.AppendLine(result.StdOut ?? string.Empty);
        sb.AppendLine("---- stderr ----");
        sb.AppendLine(result.StdErr ?? string.Empty);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        try
        {
            await os.WriteFileAsync(logPath, bytes, append: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log-file write failure must not break the run — the in-memory
            // log + reporting event still carry the information.
            logger.LogWarning(ex, "Failed to write script log to '{Path}'.", logPath);
        }
    }

    private async Task ReportProgressAsync(
        string scriptName,
        RunCommandResult result,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("script '").Append(scriptName).Append("' exit=").Append(result.ExitCode);
        if (!string.IsNullOrEmpty(result.StdOut))
            sb.AppendLine().Append("stdout: ").Append(result.StdOut);
        if (!string.IsNullOrEmpty(result.StdErr))
            sb.AppendLine().Append("stderr: ").Append(result.StdErr);
        await reporter.EmitAsync(
            new ReportingEvent.Progress(sb.ToString())
            {
                Origin = $"module:{nameof(ScriptsUserModule)}",
            },
            cancellationToken).ConfigureAwait(false);
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
