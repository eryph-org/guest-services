namespace Eryph.GuestServices.Core;

public static class Constants
{
    /// <summary>
    /// The Hyper-V integration service ID for the eryph guest services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This ID must be registered on the VM host before the eryph guest services can be used.
    /// </para>
    /// <para>
    /// This ID corresponds to the port 5002 for Linux VSock sockets.
    /// </para>
    /// </remarks>
    public static readonly Guid ServiceId = Guid.Parse("0000138a-facb-11e6-bd58-64006a7986d3");

    public static readonly string ServiceName = "Eryph Guest Services";

    /// <summary>
    /// The SCM / systemd unit name of the long-running guest-services daemon
    /// (Windows service name and Linux unit base name alike). Used by the
    /// self-updater to stop/start the service around a binary swap.
    /// </summary>
    public static readonly string DaemonServiceName = "eryph-guest-services";

    public static readonly string OperatingSystemKey = "eryph:guest-services:operating-system";

    public static readonly string StatusKey = "eryph:guest-services:status";

    public static readonly string VersionKey = "eryph:guest-services:version";

    /// <summary>
    /// The KVP key (Guest pool) where the service advertises supported optional
    /// features as a space-separated list. Hosts/tools should check this before
    /// using a feature that may be missing on older services.
    /// </summary>
    public static readonly string FeaturesKey = "eryph:guest-services:features";

    /// <summary>
    /// Feature flag advertised by services that honor the shell override KVP
    /// keys (<see cref="ShellKey"/>, <see cref="ShellArgsKey"/>).
    /// </summary>
    public static readonly string ShellOverrideFeature = "shell-override";

    /// <summary>
    /// Feature flag advertised by services that have SSH port forwarding /
    /// tunneling enabled (the opt-in <c>PortForwardingEnabled</c> switch). Absent
    /// when forwarding is off, so a client can tell whether <c>-L</c>/<c>-R</c>
    /// will be honored before attempting it.
    /// </summary>
    public static readonly string PortForwardingFeature = "port-forwarding";

    public static readonly string ClientAuthKey = "eryph:guest-services:client-public-key";

    /// <summary>
    /// Prefix for additional authorized-key slots in the External KVP pool.
    /// The guest service treats every value whose key matches
    /// <c>"eryph:guest-services:client-public-key:&lt;id&gt;"</c> as another
    /// entry in the authorized set, in addition to the legacy single slot at
    /// <see cref="ClientAuthKey"/>. The slot id is arbitrary (host name,
    /// machine id, etc.) — only the prefix is meaningful. This sidesteps the
    /// 2 KiB Hyper-V data exchange per-value limit when more than a handful
    /// of keys are authorized.
    /// </summary>
    public static readonly string ClientAuthKeyPrefix = ClientAuthKey + ":";

    /// <summary>
    /// The KVP key (External pool) that overrides the shell command spawned
    /// for an SSH session — both interactive shells and non-interactive
    /// <c>exec</c> (remote command) requests, which run the command through this
    /// shell. When unset, the service falls back to the SSH-sent <c>SHELL</c>
    /// environment variable (interactive only), then to the platform default
    /// (<c>powershell.exe</c> on Windows, <c>$SHELL</c> or <c>/bin/bash</c> on
    /// Linux).
    /// </summary>
    public static readonly string ShellKey = "eryph:guest-services:shell";

    /// <summary>
    /// The KVP key (External pool) that overrides the arguments passed to the
    /// shell command. Only consulted when <see cref="ShellKey"/> is also set.
    /// </summary>
    public static readonly string ShellArgsKey = "eryph:guest-services:shell-args";

    /// <summary>
    /// The SSH-sent environment variable name that overrides the shell command
    /// for one session. Lower priority than <see cref="ShellKey"/>.
    /// </summary>
    public static readonly string ShellEnvName = "SHELL";

    /// <summary>
    /// The SSH-sent environment variable name that overrides the shell
    /// arguments for one session. Lower priority than <see cref="ShellArgsKey"/>.
    /// </summary>
    public static readonly string ShellArgsEnvName = "SHELL_ARGS";
}
