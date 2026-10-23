namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// eryph <c>chocolatey:</c> block — installs packages with Chocolatey,
/// replacing the <c>gene:dbosoft/chocolatey:install</c> fodder. Windows-only;
/// no cloud-init equivalent (see RFC 0021).
/// </summary>
/// <remarks>
/// Unlike winget, Chocolatey runs cleanly as <c>LocalSystem</c> from
/// <c>C:\ProgramData\chocolatey\</c> — no user impersonation or wrapper. Two
/// things can request a reboot and both ride the reboot-and-continue contract
/// (RFC 0007): the bootstrap's .NET Framework reboot, and a package returning
/// <c>1641</c> / <c>3010</c>.
/// </remarks>
[CloudInitRecord]
public sealed record ChocolateyConfig
{
    /// <summary>
    /// Bootstrap Chocolatey (download + run <c>install.ps1</c>) if
    /// <c>choco.exe</c> is missing. Defaults to true to match the fodder gene.
    /// Set false on a locked-down image to skip the download and use only an
    /// already-present Chocolatey.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, YamlName = "install_chocolatey", Description = "Bootstrap Chocolatey if missing (default true)")]
    public bool? InstallChocolatey { get; init; }

    /// <summary>Custom Chocolatey sources to register (<c>choco source add</c>).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Custom Chocolatey sources to register")]
    public IReadOnlyList<ChocolateySource>? Sources { get; init; }

    /// <summary>Packages to install (<c>choco install</c>).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Chocolatey packages to install")]
    public IReadOnlyList<ChocolateyPackage>? Packages { get; init; }
}

/// <summary>A Chocolatey source (<c>choco source add -n -s --priority</c>).</summary>
[CloudInitRecord]
public sealed record ChocolateySource
{
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string Name { get; init; } = "";

    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string Url { get; init; } = "";

    /// <summary>Source priority 1..100 (lower = higher priority). Optional.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public int? Priority { get; init; }
}

/// <summary>A Chocolatey package (<c>choco install &lt;name&gt; -y</c>).</summary>
[CloudInitRecord]
public sealed record ChocolateyPackage
{
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string Name { get; init; } = "";

    /// <summary>Pinned version. Optional; latest when omitted.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string? Version { get; init; }

    /// <summary>Installer parameters passed as <c>--params</c>. Optional.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string? Params { get; init; }
}
