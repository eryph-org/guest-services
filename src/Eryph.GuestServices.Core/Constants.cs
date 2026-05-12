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

    public static readonly string ClientAuthKey = "eryph:guest-services:client-public-key";

    /// <summary>
    /// The KVP key (External pool) that overrides the shell command spawned
    /// for an interactive SSH session. When unset, the service falls back to
    /// the SSH-sent <c>SHELL</c> environment variable, then to the platform
    /// default (<c>powershell.exe</c> on Windows, <c>$SHELL</c> or
    /// <c>/bin/bash</c> on Linux).
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
