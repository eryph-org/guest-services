namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// eryph extension (not part of cloud-init): the <c>egs:</c> block configures
/// the eryph guest-services agent itself — its operator capability switches
/// (<see cref="Settings"/>) and self-update behaviour (<see cref="Update"/>).
/// Windows-only: provisioning runs only in the Windows agent (Linux guests use
/// cloud-init proper), so these directives have no Linux runtime today.
/// </summary>
[CloudInitRecord]
public sealed record EgsConfig
{
    /// <summary>
    /// Operator capability switches written to the platform-native control
    /// surface the service reads at start (Windows registry
    /// <c>HKLM\SOFTWARE\eryph\guest-services</c>).
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "eryph guest-services capability switches (remote access / provisioning / kvp auth / port forwarding)")]
    public EgsSettingsConfig? Settings { get; init; }

    /// <summary>
    /// Self-update directive: pin a version or follow a channel and let the
    /// agent replace itself at first boot.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, Description = "eryph guest-services self-update (pin a version or follow a channel)")]
    public EgsUpdateConfig? Update { get; init; }
}

/// <summary>
/// The operator capability switches, mirroring <c>IServiceControlFlags</c>.
/// Each is three-state: <c>null</c> leaves the flag untouched,
/// <c>true</c>/<c>false</c> writes the switch. The values are read at the next
/// service start, so a change made during provisioning takes effect after the
/// agent restarts — not mid-run.
/// </summary>
[CloudInitRecord]
public sealed record EgsSettingsConfig
{
    /// <summary>
    /// Gates eryph's remote-access transport (the SSH server <c>egs-tool</c>
    /// connects to). <c>false</c> turns it off.
    /// </summary>
    public bool? RemoteAccess { get; init; }

    /// <summary>
    /// Gates the first-boot provisioning agent. <c>false</c> disables further
    /// provisioning — useful to seal an image after its first instance boot.
    /// </summary>
    public bool? Provisioning { get; init; }

    /// <summary>
    /// Gates honoring of authorized client keys delivered via Hyper-V data
    /// exchange (KVP). <c>false</c> makes the locally provisioned key the sole
    /// authority over guest access.
    /// </summary>
    public bool? KvpAuth { get; init; }

    /// <summary>
    /// Gates SSH port forwarding / tunneling (<c>ssh -L</c> / <c>-R</c>) over the
    /// remote-access transport. <b>Opt-in</b>: it is off unless set to
    /// <c>true</c>, so leaving this unset keeps tunneling closed.
    /// </summary>
    public bool? PortForwarding { get; init; }
}
