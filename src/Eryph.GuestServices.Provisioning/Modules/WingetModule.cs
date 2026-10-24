using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Installs packages with the Windows Package Manager (winget), replacing the
/// <c>gene:dbosoft/winget:install</c> fodder. The agent runs as LocalSystem:
/// winget is provisioned for all users and driven through a generated wrapper,
/// so there is no user-impersonation or ordering dependency on
/// <c>UsersGroupsModule</c>. Under LocalSystem only the <c>winget</c> source
/// and machine scope work. See RFC 0020.
/// </summary>
// Config stage, before RuncmdModule: cloud-init installs packages in the config
// stage and only executes runcmd in the final stage, so packages are available
// to runcmd. Our RuncmdModule executes in Config, so the package modules must
// run earlier in Config to preserve that ordering (RFC 0020).
[Stage(Stage.Config, Order = 4, Frequency = ModuleFrequency.PerInstance)]
internal sealed class WingetModule(ILogger<WingetModule> logger) : IModule
{
    // winget exit codes treated as success (match the fodder gene):
    //   0            — installed
    //   0x8A15002B   — no newer version available (already up to date)
    //   0x8A150061   — package already installed
    private static readonly int[] SuccessExitCodes = [0, unchecked((int)0x8A15002B), unchecked((int)0x8A150061)];

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var block = userData.CloudConfig.Winget;

        // Surface the packages: umbrella's manager keys once (this module always
        // runs and is the first package module). A Linux manager is accepted and
        // no-op; an unrecognized key is almost certainly a typo that would
        // otherwise install nothing silently while provisioning reports success.
        var (ignoredLinux, unknownManagers) = PackageUmbrella.ClassifyManagers(userData.CloudConfig);
        foreach (var manager in ignoredLinux)
            logger.LogInformation(
                "packages: manager '{Manager}' is a Linux package manager; accepted but no-op on Windows.", manager);
        foreach (var manager in unknownManagers)
            logger.LogWarning(
                "packages: unrecognized manager '{Manager}' — nothing installed for it (typo?).", manager);

        // Effective package list = the winget: block plus the winget entries of
        // the packages: umbrella (RFC 0033). The module runs whenever either
        // source contributes work, even if the winget: block itself is absent.
        var packages = new List<WingetPackage>(block?.Packages ?? []);
        packages.AddRange(PackageUmbrella.WingetPackages(userData.CloudConfig));
        var sources = block?.Sources ?? [];
        var hasWork = packages.Count > 0 || sources.Count > 0;

        // Run when there is work, OR when an explicit winget: block asked to
        // provision winget (install_winget) even with no packages/sources. A
        // bare, work-free module with no block is a no-op.
        if (block is null && !hasWork)
            return ModuleOutcome.Ok();

        var install = block?.InstallWinget ?? true;

        // 1. Ensure winget is present.
        var provisioned = await context.Os.IsWingetProvisionedAsync(cancellationToken).ConfigureAwait(false);
        if (!provisioned)
        {
            if (!install)
            {
                // Operator opted out of bootstrap on a (fresh) image without
                // winget — nothing we can do, but don't fail provisioning.
                if (hasWork)
                    logger.LogWarning(
                        "winget is not provisioned and install_winget is false; skipping winget sources/packages.");
                return ModuleOutcome.Ok();
            }

            try
            {
                logger.LogInformation("winget not provisioned; provisioning the App Installer (install_winget).");
                await context.Os.ProvisionWingetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to provision winget.");
                return ModuleOutcome.Fail("winget provisioning failed.", ex);
            }
        }

        // 2. Ensure the LocalSystem wrapper exists. This is REQUIRED before any
        // RunWingetAsync call and must run even when winget was already
        // provisioned on the image (Windows 11 / Server 2025 ship it, so the
        // provisioning step above is skipped and would never write the wrapper).
        try
        {
            await context.Os.EnsureWingetWrapperAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to prepare the winget LocalSystem wrapper.");
            return ModuleOutcome.Fail("winget LocalSystem wrapper preparation failed.", ex);
        }

        // 3. Register custom sources.
        if (sources.Count > 0)
            await ProcessSourcesAsync(sources, context, cancellationToken).ConfigureAwait(false);

        // 4. Install packages.
        if (packages.Count > 0)
        {
            var failed = await ProcessPackagesAsync(packages, context, cancellationToken).ConfigureAwait(false);
            if (failed.Count > 0)
                return ModuleOutcome.Fail($"winget failed for package(s): {string.Join(", ", failed)}.");
        }

        return ModuleOutcome.Ok();
    }

    private async Task ProcessSourcesAsync(
        IReadOnlyList<WingetSource> sources,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Argument))
            {
                logger.LogWarning("Skipping winget source with empty name/argument.");
                continue;
            }

            var argv = new List<string>
            {
                "source", "add", "--name", source.Name, "--arg", source.Argument,
                "--accept-source-agreements",
            };
            if (!string.IsNullOrWhiteSpace(source.Type))
            {
                argv.Add("--type");
                argv.Add(source.Type);
            }

            logger.LogInformation("Registering winget source '{Name}'.", source.Name);
            var result = await context.Os.RunWingetAsync(argv, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                logger.LogWarning(
                    "winget source add '{Name}' returned exit code {Code}; continuing.",
                    source.Name, result.ExitCode);
        }
    }

    private async Task<IReadOnlyList<string>> ProcessPackagesAsync(
        IReadOnlyList<WingetPackage> packages,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var failed = new List<string>();
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.Id))
            {
                logger.LogWarning("Skipping winget package with empty id.");
                continue;
            }

            // Reject ids that would be read as an option or split into multiple
            // arguments by winget (a leading '-' or embedded whitespace). argv is
            // injection-safe, but such an id can never name a real package.
            if (package.Id.StartsWith('-') || package.Id.Any(char.IsWhiteSpace))
            {
                logger.LogWarning("Skipping winget package with an invalid id '{Id}'.", package.Id);
                continue;
            }

            // Under LocalSystem the `winget` source and operator-registered
            // custom sources (winget.sources) work; only `msstore` needs a
            // logged-on user token and cannot. Default to `winget`, reject
            // msstore, and guard the value so it can't inject a winget option.
            var source = string.IsNullOrWhiteSpace(package.Source) ? "winget" : package.Source.Trim();
            if (string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Skipping winget package '{Id}': the 'msstore' source is unsupported under LocalSystem.",
                    package.Id);
                continue;
            }
            // A source is one argv value, so an embedded space (a multiword
            // custom source such as "Internal Tools") is safe and must be kept;
            // only a leading '-' could be read by winget as an option.
            if (source.StartsWith('-'))
            {
                logger.LogWarning(
                    "Skipping winget package '{Id}': invalid source '{Source}'.", package.Id, source);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(package.Scope)
                && !string.Equals(package.Scope, "machine", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Skipping winget package '{Id}': scope '{Scope}' is unsupported under LocalSystem (only 'machine').",
                    package.Id, package.Scope);
                continue;
            }

            // A version is passed as the argv value after --version; guard a
            // leading '-' so it can't be read as a winget option, matching the
            // id/source guards above.
            if (!string.IsNullOrWhiteSpace(package.Version) && package.Version.StartsWith('-'))
            {
                logger.LogWarning(
                    "Skipping winget package '{Id}': invalid version '{Version}'.", package.Id, package.Version);
                continue;
            }

            var argv = new List<string>
            {
                "install", "--id", package.Id, "--exact",
                "--source", source, "--scope", "machine", "--silent",
                "--accept-source-agreements", "--accept-package-agreements",
                "--disable-interactivity",
            };
            if (!string.IsNullOrWhiteSpace(package.Version))
            {
                // Pin the version. We deliberately do NOT pass --force: it would
                // re-run the installer on an already-satisfied package and bypass
                // winget's installer integrity checks. An already-installed
                // package returns 0x8A150061, which we treat as success.
                argv.Add("--version");
                argv.Add(package.Version);
            }

            logger.LogInformation("Installing winget package '{Id}'.", package.Id);
            var result = await context.Os.RunWingetAsync(argv, cancellationToken).ConfigureAwait(false);
            if (Array.IndexOf(SuccessExitCodes, result.ExitCode) < 0)
            {
                logger.LogError(
                    "winget install '{Id}' failed with exit code {Code}.", package.Id, result.ExitCode);
                failed.Add(package.Id);
            }
        }

        return failed;
    }
}
