namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// eryph <c>dsc:</c> block — applies declarative configuration with DSC v3
/// (the <c>microsoft/DSC</c> <c>dsc.exe</c> engine). Replaces the
/// <c>gene:dbosoft/windsc</c> workarounds (NuGet bootstrap + self-signed MOF
/// cert), neither of which DSC v3 needs. Windows-only; cloud-init never grew a
/// DSC module (see RFC 0025).
/// </summary>
/// <remarks>
/// The agent runs as LocalSystem and runs after <c>RuncmdModule</c>, so an
/// operator runcmd / <c>write_files</c> can stage configuration documents
/// first. The native <c>dsc.exe</c> config engine does not need PowerShell,
/// but PowerShell-based resources and the <c>PSDesiredStateConfiguration</c>
/// (DSC v1) adapter require pwsh 7.2+, which Windows Server ships without — the
/// module warns when pwsh is missing.
/// </remarks>
[CloudInitRecord]
public sealed record DscConfig
{
    /// <summary>
    /// Whether to install DSC v3 when <c>dsc.exe</c> is missing: <c>auto</c>
    /// (default) installs it via <see cref="InstallSource"/>; <c>skip</c> fails
    /// fast with a clear error rather than silently no-op'ing.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Install DSC v3 if missing: 'auto' (default) or 'skip'")]
    public string? Install { get; init; }

    /// <summary>
    /// Where to obtain <c>dsc.exe</c>: <c>winget</c> (default, winget id
    /// <c>Microsoft.DSC</c>), <c>github_release</c> (download the signed asset
    /// from <c>PowerShell/DSC</c> releases), or <c>path</c> (use the operator-
    /// installed executable named by <see cref="Path"/> — useful offline).
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Where to get dsc.exe: 'winget' (default), 'github_release', or 'path'")]
    public string? InstallSource { get; init; }

    /// <summary>Optional version pin (e.g. <c>3.1.0</c>); latest when omitted or <c>latest</c>.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Optional dsc.exe version pin (e.g. '3.1.0'); latest when omitted")]
    public string? InstallVersion { get; init; }

    /// <summary>
    /// Explicit path to an operator-installed <c>dsc.exe</c>, used with
    /// <c>install_source: path</c>. The path must resolve to a trusted location
    /// (the agent executes it as LocalSystem).
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Explicit path to dsc.exe for install_source: path")]
    public string? Path { get; init; }

    /// <summary>The DSC v3 configuration documents to apply, in order.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "DSC v3 configuration documents to apply")]
    public IReadOnlyList<DscConfiguration>? Configurations { get; init; }

    /// <summary>
    /// The <c>dsc.exe config</c> verb: <c>set</c> (default, converge),
    /// <c>test</c> (dry-run / report drift), or <c>get</c> (inspect state).
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "dsc.exe config verb: 'set' (default), 'test', or 'get'")]
    public string? Apply { get; init; }

    /// <summary>
    /// When true, keep applying later configurations after one fails; when false
    /// (default) stop at the first failed configuration.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Continue applying later configurations after one fails (default false)")]
    public bool? ContinueOnError { get; init; }
}

/// <summary>
/// A single DSC v3 configuration document. Exactly one of <see cref="Document"/>,
/// <see cref="DocumentUrl"/> or <see cref="DocumentPath"/> must be set.
/// </summary>
[CloudInitRecord]
public sealed record DscConfiguration
{
    /// <summary>A label for logging (also used to name the staged document file).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows)]
    public string Name { get; init; } = "";

    /// <summary>Inline DSC v3 YAML configuration document.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Inline DSC v3 YAML configuration document")]
    public string? Document { get; init; }

    /// <summary>URL to fetch the configuration document from at apply time.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "URL to fetch the configuration document from at apply time")]
    public string? DocumentUrl { get; init; }

    /// <summary>
    /// Path to a configuration document already on the guest (e.g. staged via
    /// <c>write_files</c>). Cloud-init style unix paths are translated.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Path to a configuration document already on the guest")]
    public string? DocumentPath { get; init; }

    /// <summary>
    /// Optional <c>sha256:...</c> integrity check applied to a
    /// <see cref="DocumentUrl"/> download before the document is used.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "Optional 'sha256:...' integrity check for document_url")]
    public string? Checksum { get; init; }
}
