namespace Eryph.GuestServices.Core;

/// <summary>
/// Well-known filesystem paths for the guest-services agent as a whole — the
/// <c>egs-service</c> process, which hosts both remote access (SSH) and
/// provisioning. These are deliberately separate from the provisioning-only
/// state tree (<c>%ProgramData%\eryph\provisioning</c>): the operational log
/// is a global agent concern, not a provisioning artefact.
/// </summary>
public static class AgentPaths
{
    // Test-only hook. Production code leaves this null and uses the
    // platform-default location below.
    internal static string? RootOverride { get; set; }

    /// <summary>
    /// Directory holding the agent's operational log(s). Sits alongside the
    /// service-wide config root that stores the SSH keys.
    /// <list type="bullet">
    /// <item>Windows: <c>%ProgramData%\eryph\guest-services\logs</c></item>
    /// <item>Linux: <c>/var/log/eryph/guest-services</c></item>
    /// </list>
    /// </summary>
    public static string LogsDirectory =>
        RootOverride is not null
            ? Path.Combine(RootOverride, "logs")
            : OperatingSystem.IsWindows()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "eryph", "guest-services", "logs")
                : "/var/log/eryph/guest-services";

    /// <summary>The agent's operational log file (<c>agent.log</c>).</summary>
    public static string LogFile => Path.Combine(LogsDirectory, "agent.log");

    /// <summary>
    /// Working directory for self-update staging (downloaded + extracted
    /// payloads, per target version). Sits alongside <see cref="LogsDirectory"/>
    /// under the service-wide config root.
    /// <list type="bullet">
    /// <item>Windows: <c>%ProgramData%\eryph\guest-services\update</c></item>
    /// <item>Linux: <c>/var/lib/eryph/guest-services/update</c></item>
    /// </list>
    /// </summary>
    public static string UpdateDirectory =>
        RootOverride is not null
            ? Path.Combine(RootOverride, "update")
            : OperatingSystem.IsWindows()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "eryph", "guest-services", "update")
                : "/var/lib/eryph/guest-services/update";
}
