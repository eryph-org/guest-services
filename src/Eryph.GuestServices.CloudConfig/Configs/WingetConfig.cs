namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// eryph <c>winget:</c> block — the cloud-config surface that replaces the
/// <c>gene:dbosoft/winget:install</c> fodder. Installs packages with the
/// Windows Package Manager. Windows-only; there is no cloud-init or
/// cloudbase-init equivalent (see RFC 0020).
/// </summary>
/// <remarks>
/// The provisioning agent runs as LocalSystem. winget is <em>provisioned</em>
/// for all users (not installed per-user, which LocalSystem cannot do) and
/// driven through a generated LocalSystem wrapper. Under LocalSystem only the
/// <c>winget</c> source and <c>--scope machine</c> work; the <c>msstore</c>
/// source and per-user scope need a logged-on user token and are rejected with
/// a warning.
/// </remarks>
[CloudInitRecord]
public sealed record WingetConfig
{
    /// <summary>
    /// Provision the App Installer (winget) for all users if it is missing.
    /// Defaults to true to match the fodder gene, which always installs
    /// winget. Set false on a locked-down image to skip the GitHub download
    /// and configure only an already-present winget.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, YamlName = "install_winget", Description = "Provision the App Installer (winget) for all users if missing (default true)")]
    public bool? InstallWinget { get; init; }

    /// <summary>Custom winget sources to register before installing packages.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Custom winget sources to register")]
    public IReadOnlyList<WingetSource>? Sources { get; init; }

    /// <summary>Packages to install machine-wide from the winget source.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "winget packages to install (machine scope, winget source)")]
    public IReadOnlyList<WingetPackage>? Packages { get; init; }
}

/// <summary>A custom winget source (<c>winget source add</c>).</summary>
[CloudInitRecord]
public sealed record WingetSource
{
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string Name { get; init; } = "";

    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string Argument { get; init; } = "";

    /// <summary>Source type, e.g. <c>Microsoft.Rest</c>. Optional.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string? Type { get; init; }
}

/// <summary>A winget package to install (<c>winget install</c>).</summary>
[CloudInitRecord]
public sealed record WingetPackage
{
    /// <summary>The winget package id, e.g. <c>Git.Git</c>.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string Id { get; init; } = "";

    /// <summary>Pinned version. Optional; latest when omitted.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string? Version { get; init; }

    /// <summary>
    /// Source: defaults to <c>winget</c>. <c>msstore</c> is unsupported under
    /// LocalSystem and such a package is skipped with a warning.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string? Source { get; init; }

    /// <summary>
    /// Scope: defaults to <c>machine</c>. <c>user</c> is unsupported under
    /// LocalSystem (it would land in the system profile) and such a package is
    /// skipped with a warning.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string? Scope { get; init; }
}
