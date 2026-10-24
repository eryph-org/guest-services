using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Applies declarative configuration with DSC v3 (<c>dsc.exe</c>), replacing the
/// <c>gene:dbosoft/windsc</c> workarounds. The agent runs as LocalSystem. dsc.exe
/// is installed on demand via the winget module path (id <c>Microsoft.DSC</c>), a
/// GitHub release, or pointed at an operator-installed executable; the engine is
/// native and needs no NuGet bootstrap or MOF-encryption certificate. DSC v1
/// resources run through v3's <c>PSDesiredStateConfiguration</c> adapter, which
/// (like any PowerShell-based resource) needs pwsh 7.2+ — the module warns when
/// it is missing. See RFC 0025.
/// </summary>
// Final stage: cloud-init-style, convergence runs after the Config stage, so an
// operator runcmd (Config/6) or deferred write_files (Final/-1) can stage
// configuration documents first. Order 1 is after ScriptsUserModule (0) and the
// deferred write-files (-1), well before PowerStateModule (end of Final).
[Stage(Stage.Final, Order = 1, Frequency = ModuleFrequency.PerInstance)]
internal sealed class DscModule(ILogger<DscModule> logger) : IModule
{
    // winget exit codes treated as success (match WingetModule):
    //   0            — installed
    //   0x8A15002B   — no newer version available
    //   0x8A150061   — package already installed
    private static readonly int[] WingetSuccessExitCodes =
        [0, unchecked((int)0x8A15002B), unchecked((int)0x8A150061)];

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var block = userData.CloudConfig.Dsc;
        if (block is null)
            return ModuleOutcome.Ok();

        var configurations = block.Configurations ?? [];
        if (configurations.Count == 0)
        {
            // Installing dsc.exe with nothing to converge is pointless; a bare
            // dsc: block with no configurations is a no-op.
            logger.LogInformation("dsc: no configurations to apply.");
            return ModuleOutcome.Ok();
        }

        var verb = NormalizeVerb(block.Apply);
        if (verb is null)
            return ModuleOutcome.Fail($"dsc: invalid apply verb '{block.Apply}' (expected set / test / get).");

        // 1. Resolve dsc.exe, installing it if missing and install is 'auto'.
        var dscPath = await context.Os.ResolveDscExecutableAsync(block.Path, cancellationToken).ConfigureAwait(false);
        if (dscPath is null)
        {
            var install = block.Install ?? "auto";
            if (string.Equals(install, "skip", StringComparison.OrdinalIgnoreCase))
                return ModuleOutcome.Fail("dsc: dsc.exe is not installed and install is 'skip'.");

            if (await InstallDscAsync(block, context, cancellationToken).ConfigureAwait(false) is { } failure)
                return failure;

            dscPath = await context.Os.ResolveDscExecutableAsync(block.Path, cancellationToken).ConfigureAwait(false);
            if (dscPath is null)
                return ModuleOutcome.Fail("dsc: dsc.exe could not be located after installation.");
        }

        // 2. Warn (do not fail) when pwsh is missing. The native engine runs, but
        // PowerShell-based resources / the DSC v1 adapter need pwsh 7.2+.
        if (!await context.Os.IsPowerShell7AvailableAsync(cancellationToken).ConfigureAwait(false))
            logger.LogWarning(
                "dsc: PowerShell 7 (pwsh) was not found. The native dsc.exe engine works, but "
                + "PowerShell-based resources and the PSDesiredStateConfiguration (DSC v1) adapter "
                + "require pwsh 7.2+. Install it via winget/chocolatey if your configurations use them.");

        // 3. Apply each configuration in order.
        var continueOnError = block.ContinueOnError ?? false;
        var rebootRequested = false;
        var failed = new List<string>();

        foreach (var configuration in configurations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(configuration.Name) ? "<unnamed>" : configuration.Name;

            string documentPath;
            try
            {
                documentPath = await ResolveDocumentPathAsync(configuration, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Genuine provisioning cancellation — abort the whole module.
                throw;
            }
            catch (Exception ex)
            {
                // Anything else, INCLUDING a document-download timeout (its own
                // internal CTS surfaces as OperationCanceledException while the
                // provisioning token is still live), is a per-configuration
                // failure that must honour continue_on_error.
                logger.LogError(ex, "dsc: failed to stage configuration '{Name}'.", name);
                failed.Add(name);
                if (!continueOnError)
                    break;
                continue;
            }

            logger.LogInformation("dsc: applying configuration '{Name}' ({Verb}).", name, verb);
            DscApplyResult result;
            try
            {
                result = await context.Os.RunDscConfigAsync(dscPath, documentPath, verb, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // RunDscConfigAsync can throw, not just return a non-zero exit —
                // e.g. it refuses an untrusted document_path. That must be a
                // per-configuration failure honouring continue_on_error, not an
                // unhandled exception that aborts the whole provisioning run.
                logger.LogError(ex, "dsc: applying configuration '{Name}' failed.", name);
                failed.Add(name);
                if (!continueOnError)
                    break;
                continue;
            }

            if (result.ExitCode != 0)
            {
                logger.LogError(
                    "dsc: configuration '{Name}' failed with exit code {Code}.{Detail}",
                    name, result.ExitCode, StreamDetail(result.StdErr, result.JsonOutput));
                failed.Add(name);
                if (!continueOnError)
                    break;
                continue;
            }

            // 'get' / 'test' are read-only: their value is the reported state /
            // drift, which would otherwise be discarded. Surface it on the
            // provisioning log (capped) so an operator can see the result.
            if (verb is "get" or "test")
                LogInspectionResult(name, verb, result.JsonOutput);

            if (result.RebootRequested)
            {
                // Only an actual apply ('set') acts on a restart request. 'get' /
                // 'test' are read-only: a configuration under inspection may
                // report a pending restart (DSC surfaces restartRequired in their
                // output too), and inspecting a guest must never reboot it.
                if (verb == "set")
                {
                    logger.LogInformation("dsc: configuration '{Name}' requested a reboot.", name);
                    rebootRequested = true;
                }
                else
                {
                    logger.LogInformation(
                        "dsc: configuration '{Name}' ({Verb}) reports a pending restart; not acting on it for a read-only verb.",
                        name, verb);
                }
            }
        }

        // A reboot is only honoured when every configuration otherwise succeeded
        // (RFC 0025). Reboot-and-continue re-enters this PerInstance module; DSC
        // set is idempotent, so already-converged resources are no-ops and a
        // resource that asked for a reboot reports none after it.
        if (rebootRequested && failed.Count == 0)
            return ModuleOutcome.Reboot("A DSC configuration requested a reboot.");
        if (failed.Count > 0)
            return ModuleOutcome.Fail($"dsc failed for configuration(s): {string.Join(", ", failed)}.");

        return ModuleOutcome.Ok();
    }

    // Installs dsc.exe per install_source. Returns a Fail outcome on error, or
    // null on success. winget reuses the IWindowsOs winget methods (the same
    // LocalSystem path WingetModule uses); github_release downloads the signed
    // asset; path means the operator already installed it (and ResolveDsc
    // already found it untrusted / missing, so this is an error).
    private async Task<ModuleOutcome?> InstallDscAsync(
        DscConfig block,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var source = block.InstallSource ?? "winget";

        switch (source.Trim().ToLowerInvariant())
        {
            case "winget":
                return await InstallDscViaWingetAsync(block, context, cancellationToken).ConfigureAwait(false);

            case "github_release":
                try
                {
                    logger.LogInformation("dsc: installing DSC v3 from the GitHub release.");
                    await context.Os.InstallDscFromGithubReleaseAsync(block.InstallVersion, cancellationToken)
                        .ConfigureAwait(false);
                    return null;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "dsc: GitHub release install failed.");
                    return ModuleOutcome.Fail("dsc: GitHub release install failed.", ex);
                }

            case "path":
                // ResolveDscExecutableAsync(block.Path) already returned null — the
                // configured executable is missing or on an untrusted path.
                return ModuleOutcome.Fail(
                    $"dsc: install_source is 'path' but dsc.exe was not found or not trusted at '{block.Path ?? "<unset>"}'.");

            default:
                return ModuleOutcome.Fail($"dsc: unknown install_source '{source}' (expected winget / github_release / path).");
        }
    }

    private async Task<ModuleOutcome?> InstallDscViaWingetAsync(
        DscConfig block,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("dsc: installing DSC v3 via winget (Microsoft.DSC).");
            if (!await context.Os.IsWingetProvisionedAsync(cancellationToken).ConfigureAwait(false))
                await context.Os.ProvisionWingetAsync(cancellationToken).ConfigureAwait(false);
            await context.Os.EnsureWingetWrapperAsync(cancellationToken).ConfigureAwait(false);

            var argv = new List<string>
            {
                "install", "--id", "Microsoft.DSC", "--exact",
                "--source", "winget", "--scope", "machine", "--silent",
                "--accept-source-agreements", "--accept-package-agreements",
                "--disable-interactivity",
            };
            if (!string.IsNullOrWhiteSpace(block.InstallVersion)
                && !string.Equals(block.InstallVersion.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
            {
                argv.Add("--version");
                argv.Add(block.InstallVersion);
            }

            var result = await context.Os.RunWingetAsync(argv, cancellationToken).ConfigureAwait(false);
            if (Array.IndexOf(WingetSuccessExitCodes, result.ExitCode) < 0)
                return ModuleOutcome.Fail(
                    $"dsc: winget install of Microsoft.DSC failed (exit {result.ExitCode}).{StreamDetail(result.StdErr, result.StdOut)}");

            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "dsc: winget install of Microsoft.DSC failed.");
            return ModuleOutcome.Fail("dsc: winget install of Microsoft.DSC failed.", ex);
        }
    }

    // Stages the configuration's document and returns the path to feed dsc.exe.
    // Exactly one of document / document_url / document_path must be set.
    private static async Task<string> ResolveDocumentPathAsync(
        DscConfiguration configuration,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var sourceCount =
            (string.IsNullOrWhiteSpace(configuration.Document) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(configuration.DocumentUrl) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(configuration.DocumentPath) ? 0 : 1);
        if (sourceCount != 1)
            throw new InvalidOperationException(
                "a configuration must set exactly one of document / document_url / document_path.");

        var fileName = DocumentFileName(configuration.Name);

        if (!string.IsNullOrWhiteSpace(configuration.Document))
            return await context.Os.WriteDscDocumentAsync(fileName, configuration.Document, cancellationToken)
                .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(configuration.DocumentUrl))
            return await context.Os.DownloadDscDocumentAsync(
                    fileName, configuration.DocumentUrl, configuration.Checksum, cancellationToken)
                .ConfigureAwait(false);

        // document_path: an operator-staged file. Translate cloud-init unix paths.
        return context.Os.TranslateUnixPath(configuration.DocumentPath!);
    }

    // A safe, deterministic file name derived from the configuration name, so an
    // operator-chosen name cannot escape the hardened document directory.
    private static string DocumentFileName(string? name)
    {
        var slug = string.IsNullOrWhiteSpace(name) ? "configuration" : name.Trim();
        var chars = slug.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return new string(chars) + ".dsc.yaml";
    }

    // Logs the dsc.exe output of a read-only verb (get / test) on the
    // provisioning log, capped so a large state document cannot flood it.
    private void LogInspectionResult(string name, string verb, string? jsonOutput)
    {
        var output = Truncate(jsonOutput, 4000);
        if (output.Length == 0)
        {
            logger.LogInformation("dsc: configuration '{Name}' ({Verb}) produced no output.", name, verb);
            return;
        }

        logger.LogInformation(
            "dsc: configuration '{Name}' ({Verb}) result:\n{Result}", name, verb, output);
    }

    // Bounded stderr / stdout detail appended to a failure message so an
    // operator can diagnose a failed run from the provisioning log, rather than
    // seeing only a numeric exit code. Empty when both streams are empty.
    private static string StreamDetail(string? stdErr, string? stdOut)
    {
        var parts = new List<string>();
        var err = Truncate(stdErr, 2000);
        if (err.Length > 0)
            parts.Add($"stderr: {err}");
        var outp = Truncate(stdOut, 2000);
        if (outp.Length > 0)
            parts.Add($"output: {outp}");
        return parts.Count == 0 ? "" : "\n  " + string.Join("\n  ", parts);
    }

    private static string Truncate(string? value, int maxChars)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length > maxChars
            ? trimmed[..maxChars] + $" …(+{trimmed.Length - maxChars} more chars)"
            : trimmed;
    }

    private static string? NormalizeVerb(string? apply)
    {
        var verb = (apply ?? "set").Trim().ToLowerInvariant();
        return verb is "set" or "test" or "get" ? verb : null;
    }
}
