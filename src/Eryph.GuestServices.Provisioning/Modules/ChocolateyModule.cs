using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Installs packages with Chocolatey, replacing the
/// <c>gene:dbosoft/chocolatey:install</c> fodder. Chocolatey runs as
/// LocalSystem directly (no wrapper / impersonation, unlike winget). Two things
/// can request a reboot and both ride the reboot-and-continue contract
/// (RFC 0007): the bootstrap's .NET Framework reboot and a package returning
/// <c>1641</c> / <c>3010</c>. Ordered after <see cref="WingetModule"/> so an
/// operator who lists both gets winget first. See RFC 0021.
/// </summary>
// Config stage, after WingetModule and before RuncmdModule (see the ordering
// note on WingetModule): packages must be installed before runcmd executes.
[Stage(Stage.Config, Order = 5, Frequency = ModuleFrequency.PerInstance)]
internal sealed class ChocolateyModule(ILogger<ChocolateyModule> logger) : IModule
{
    // choco exit codes that mean "installed, but reboot required".
    private static readonly int[] RebootExitCodes = [1641, 3010];

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig.Chocolatey;
        if (config is null)
            return ModuleOutcome.Ok();

        // Validate up front — bad input is an operator error, not a per-package
        // continue-on-failure case.
        if (Validate(config) is { } validationError)
            return validationError;

        var hasWork = config.Packages is { Count: > 0 } || config.Sources is { Count: > 0 };
        var install = config.InstallChocolatey ?? true;

        // 1. Ensure choco is present.
        var installed = await context.Os.IsChocolateyInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (!installed)
        {
            if (!install)
            {
                if (hasWork)
                    logger.LogWarning(
                        "Chocolatey is not installed and install_chocolatey is false; skipping sources/packages.");
                return ModuleOutcome.Ok();
            }

            try
            {
                logger.LogInformation("Chocolatey not installed; bootstrapping (install_chocolatey).");
                var bootstrap = await context.Os.BootstrapChocolateyAsync(cancellationToken).ConfigureAwait(false);
                if (bootstrap.RebootRequired)
                    // Reboot-and-continue re-enters the module; choco is then
                    // present and sources/packages proceed on the next boot.
                    return ModuleOutcome.Reboot("Chocolatey bootstrap requires a reboot (.NET Framework).");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to bootstrap Chocolatey.");
                return ModuleOutcome.Fail("Chocolatey bootstrap failed.", ex);
            }
        }

        // 2. Register custom sources.
        if (config.Sources is { Count: > 0 })
            await ProcessSourcesAsync(config.Sources, context, cancellationToken).ConfigureAwait(false);

        // 3. Install packages.
        if (config.Packages is { Count: > 0 })
        {
            var (failed, rebootRequested) =
                await ProcessPackagesAsync(config.Packages, context, cancellationToken).ConfigureAwait(false);
            // Reboot takes precedence over a failure: a package legitimately
            // asked for a reboot, and reboot-and-continue re-enters the module,
            // which retries the failed packages (already-installed ones are
            // no-ops). A still-failing package then surfaces as Fail on the
            // next pass.
            if (rebootRequested)
                return ModuleOutcome.Reboot("A Chocolatey package requested a reboot (1641/3010).");
            if (failed.Count > 0)
                return ModuleOutcome.Fail($"Chocolatey failed for package(s): {string.Join(", ", failed)}.");
        }

        return ModuleOutcome.Ok();
    }

    private static ModuleOutcome? Validate(ChocolateyConfig config)
    {
        if (config.Packages is not null)
        {
            foreach (var package in config.Packages)
            {
                if (string.IsNullOrWhiteSpace(package.Name))
                    return ModuleOutcome.Fail("chocolatey: a package entry has no 'name'.");
            }
        }

        if (config.Sources is not null)
        {
            foreach (var source in config.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Url))
                    return ModuleOutcome.Fail("chocolatey: a source entry is missing 'name' or 'url'.");
                if (source.Priority is { } p && (p < 1 || p > 100))
                    return ModuleOutcome.Fail($"chocolatey: source '{source.Name}' priority {p} is outside 1..100.");
            }
        }

        return null;
    }

    private async Task ProcessSourcesAsync(
        IReadOnlyList<ChocolateySource> sources,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            var argv = new List<string> { "source", "add", "-n", source.Name, "-s", source.Url };
            if (source.Priority is { } priority)
            {
                argv.Add("--priority");
                argv.Add(priority.ToString());
            }

            logger.LogInformation("Registering Chocolatey source '{Name}'.", source.Name);
            var result = await context.Os.RunChocoAsync(argv, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                logger.LogWarning(
                    "choco source add '{Name}' returned exit code {Code}; continuing.",
                    source.Name, result.ExitCode);
        }
    }

    private async Task<(IReadOnlyList<string> Failed, bool RebootRequested)> ProcessPackagesAsync(
        IReadOnlyList<ChocolateyPackage> packages,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var failed = new List<string>();
        var rebootRequested = false;

        foreach (var package in packages)
        {
            var argv = new List<string> { "install", package.Name, "-y", "--no-progress" };
            if (!string.IsNullOrWhiteSpace(package.Version))
            {
                argv.Add("--version");
                argv.Add(package.Version);
            }
            if (!string.IsNullOrWhiteSpace(package.Params))
                // Single --params=<value> element: choco's option parser is
                // quirky with a space-separated value that contains quotes, and
                // the inner install params commonly do (e.g. "/InstallDir:C:\x").
                argv.Add($"--params={package.Params}");

            logger.LogInformation("Installing Chocolatey package '{Name}'.", package.Name);
            var result = await context.Os.RunChocoAsync(argv, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
                continue;
            if (Array.IndexOf(RebootExitCodes, result.ExitCode) >= 0)
            {
                logger.LogInformation(
                    "choco install '{Name}' succeeded and requested a reboot (exit {Code}).",
                    package.Name, result.ExitCode);
                rebootRequested = true;
                continue;
            }

            logger.LogError("choco install '{Name}' failed with exit code {Code}.", package.Name, result.ExitCode);
            failed.Add(package.Name);
        }

        return (failed, rebootRequested);
    }
}
