namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Cloud-init <c>ssh:</c> block. Cloud-init only defines
/// <c>emit_keys_to_console</c> here today; eryph extends the block with
/// Windows-specific knobs (currently <c>install_openssh</c>). The broader
/// host-key / pwauth / authorized-keys surface stays at the top level
/// (<c>ssh_keys</c>, <c>ssh_pwauth</c>, <c>ssh_authorized_keys</c>, ...) to
/// match cloud-init's <c>cc_ssh</c> schema — see RFC 0018.
/// </summary>
[CloudInitRecord]
public sealed record SshConfig
{
    /// <summary>
    /// Cloud-init <c>ssh.emit_keys_to_console</c>. On Linux this controls
    /// whether the freshly generated host-key fingerprints are echoed to the
    /// system console. On Windows the SshModule maps it to the
    /// <c>SshHostKeysReported</c> reporting event: when false the host-key
    /// fingerprints are not surfaced to the operator console. Defaults to
    /// true (cloud-init's default) when omitted.
    /// </summary>
    [CloudInitField(YamlName = "emit_keys_to_console", Description = "Surface ssh host-key fingerprints to the console / reporting channel")]
    public bool? EmitKeysToConsole { get; init; }

    /// <summary>
    /// eryph extension (not part of cloud-init's <c>cc_ssh</c> schema). When
    /// true the SshModule installs the Win32-OpenSSH server capability
    /// (<c>Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0</c>)
    /// and sets the <c>sshd</c> service to start Automatically. When omitted /
    /// false the module only configures an already-installed sshd
    /// (detect-don't-install). Windows-only.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Windows, YamlName = "install_openssh", Description = "eryph extension: install the Win32-OpenSSH server capability if missing")]
    public bool? InstallOpenssh { get; init; }
}
