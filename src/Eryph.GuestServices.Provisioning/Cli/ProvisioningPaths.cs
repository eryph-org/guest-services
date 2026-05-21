using Eryph.GuestServices.Provisioning.Configuration;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Resolves the well-known paths the CLI needs to inspect or clean up.
/// Centralised so reset / collect-logs / status all agree on where to look.
/// Tests can override the root by setting <see cref="RootOverride"/>.
/// </summary>
internal static class ProvisioningPaths
{
    // Test-only hook. Production code leaves this null and uses
    // SpecialFolder.CommonApplicationData (i.e. %ProgramData% on Windows).
    internal static string? RootOverride { get; set; }

    public static string Root =>
        RootOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "provisioning");

    public static string StateFile => Path.Combine(Root, "state.json");

    public static string LogsDirectory => Path.Combine(Root, "logs");

    public static string ScriptsDirectory(ProvisioningSettings settings) =>
        Environment.ExpandEnvironmentVariables(settings.Scripts.PerInstanceDirectory);
}
