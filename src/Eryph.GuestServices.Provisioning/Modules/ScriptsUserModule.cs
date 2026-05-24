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
    /// <summary>
    /// Exit code reserved by the cloudbase-init "reboot and continue"
    /// convention. Same value the RuncmdModule honors.
    /// </summary>
    public const int RebootRequestedExitCode = 1003;

    /// <summary>
    /// Per-script reboot quota. A given (ordinal, body-hash) may request
    /// reboot at most this many times before we fail the module — guards
    /// against a broken installer that returns 1003 indefinitely.
    /// docs/bugs/0001 "loop-safety". Sourced from
    /// ProvisioningSettings.Reboot.MaxPerScript (default 2).
    /// </summary>
    internal int MaxRebootsPerScript => settings.Reboot.MaxPerScript;

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

        var scriptDirectory = Environment.ExpandEnvironmentVariables(settings.Scripts.PerInstanceDirectory);
        var logDirectory = Environment.ExpandEnvironmentVariables(LogsDirectory);
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

            var bodyHash = ScriptCheckpoint.ComputeBodyHash(script.Body);

            // Resume guard: skip scripts already recorded as executed (cloud-init
            // doesn't have this; we do because cbi-style 1003 means the script
            // DID its work and signalled "reboot, then move on"). The (ordinal,
            // body-hash) pair invalidates the entry if the operator edits the
            // script between runs.
            if (checkpoint.Contains(ordinal, bodyHash))
            {
                logger.LogInformation(
                    "Skipping script #{Index} ('{Filename}') — already executed in a prior run (resume).",
                    ordinal, script.Filename ?? "<root>");
                continue;
            }

            var scriptName = BuildScriptName(ordinal, script);
            var scriptPath = Path.Combine(scriptDirectory, scriptName);

            try
            {
                await context.Os.WriteFileAsync(scriptPath, script.Body, append: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to stage script #{Index} to '{Path}'; skipping.", ordinal, scriptPath);
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
                    ordinal, scriptPath, script.Kind);
                continue;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Script #{Index} ({Path}) failed to start.", ordinal, scriptPath);
                continue;
            }

            await WriteLogAsync(context.Os, logDirectory, scriptName, scriptPath, result, cancellationToken)
                .ConfigureAwait(false);
            LogResult(ordinal, scriptPath, result);
            await ReportProgressAsync(scriptName, result, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == RebootRequestedExitCode)
            {
                // Per-script reboot quota: the same (ordinal, body-hash) may
                // request reboot at most MaxRebootsPerScript times. Past that
                // we treat it as a stuck installer and fail rather than loop.
                var rebootKey = $"{ordinal}:{bodyHash}";
                var rebootCount = checkpoint.RebootCounts.GetValueOrDefault(rebootKey) + 1;
                if (rebootCount > MaxRebootsPerScript)
                {
                    logger.LogError(
                        "Script #{Index} ({Path}) exceeded per-script reboot quota ({Quota}). Failing.",
                        ordinal, scriptPath, MaxRebootsPerScript);
                    var updatedCounts = new Dictionary<string, int>(checkpoint.RebootCounts, StringComparer.Ordinal)
                    {
                        [rebootKey] = rebootCount,
                    };
                    await checkpointStore.SaveAsync(instanceId,
                        checkpoint with { RebootCounts = updatedCounts },
                        cancellationToken).ConfigureAwait(false);
                    return ModuleOutcome.Fail(
                        $"script #{ordinal} ('{scriptName}') exceeded per-script reboot quota.");
                }

                logger.LogInformation(
                    "Script #{Index} ({Path}) requested reboot-and-continue (exit {Code}).",
                    ordinal, scriptPath, RebootRequestedExitCode);

                // Mark the script as executed BEFORE returning Reboot — cbi
                // semantics for 1003: "I did my work; reboot, then run my
                // successors". The resume must NOT re-run this script.
                var executed = checkpoint.Executed
                    .Append(new ScriptCheckpointEntry(ordinal, bodyHash))
                    .ToList();
                var counts = new Dictionary<string, int>(checkpoint.RebootCounts, StringComparer.Ordinal)
                {
                    [rebootKey] = rebootCount,
                };
                checkpoint = checkpoint with { Executed = executed, RebootCounts = counts };
                await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);

                return ModuleOutcome.Reboot(
                    $"script #{ordinal} ('{scriptName}') requested reboot (exit {RebootRequestedExitCode}).");
            }

            if (result.ExitCode != 0)
                logger.LogError(
                    "Script #{Index} ({Path}) exited with code {Code}; continuing with remaining scripts.",
                    ordinal, scriptPath, result.ExitCode);

            // Mark executed (success OR non-1003 failure). A failing script
            // does not block the queue, so it has 'run'; replaying it on the
            // next reboot-resume would just fail twice.
            var nowExecuted = checkpoint.Executed
                .Append(new ScriptCheckpointEntry(ordinal, bodyHash))
                .ToList();
            checkpoint = checkpoint with { Executed = nowExecuted };
            await checkpointStore.SaveAsync(instanceId, checkpoint, cancellationToken).ConfigureAwait(false);
        }

        return ModuleOutcome.Ok();
    }

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
                cancellationToken).ConfigureAwait(false),

            ScriptKind.Cmd => await context.Os.RunArgvCommandAsync(
                ["cmd.exe", "/c", scriptPath],
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
        var logPath = Path.Combine(logDirectory, scriptName + ".log");
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
