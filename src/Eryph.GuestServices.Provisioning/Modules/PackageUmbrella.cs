using Eryph.GuestServices.CloudConfig;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Resolves the cloud-init-compatible <c>packages:</c> umbrella (RFC 0033) into
/// the per-provider package lists consumed by <see cref="WingetModule"/> and
/// <see cref="ChocolateyModule"/>. Bare names / <c>[name, version]</c> pairs use
/// the default provider (winget); manager groups route to their provider; Linux
/// managers (apt/snap/yum/…) are ignored on Windows.
/// </summary>
internal static class PackageUmbrella
{
    private static readonly HashSet<string> WingetManagers =
        new(StringComparer.OrdinalIgnoreCase) { "winget" };

    private static readonly HashSet<string> ChocolateyManagers =
        new(StringComparer.OrdinalIgnoreCase) { "chocolatey", "choco" };

    /// <summary>winget packages contributed by the <c>packages:</c> umbrella.</summary>
    public static IReadOnlyList<WingetPackage> WingetPackages(CloudConfigModel config)
    {
        var result = new List<WingetPackage>();
        foreach (var entry in config.Packages ?? [])
        {
            if (entry.Managers is { } managers)
            {
                foreach (var (manager, items) in managers)
                {
                    if (!WingetManagers.Contains(manager))
                        continue;
                    foreach (var item in items)
                        if (!string.IsNullOrWhiteSpace(item.Name))
                            result.Add(new WingetPackage { Id = item.Name, Version = item.Version });
                }
            }
            else if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                // Bare name / [name, version] -> default provider (winget).
                result.Add(new WingetPackage { Id = entry.Name, Version = entry.Version });
            }
        }

        return result;
    }

    /// <summary>chocolatey packages contributed by the <c>packages:</c> umbrella.</summary>
    public static IReadOnlyList<ChocolateyPackage> ChocolateyPackages(CloudConfigModel config)
    {
        var result = new List<ChocolateyPackage>();
        foreach (var entry in config.Packages ?? [])
        {
            // Bare names go to the default provider (winget), not chocolatey.
            if (entry.Managers is not { } managers)
                continue;
            foreach (var (manager, items) in managers)
            {
                if (!ChocolateyManagers.Contains(manager))
                    continue;
                foreach (var item in items)
                    if (!string.IsNullOrWhiteSpace(item.Name))
                        result.Add(new ChocolateyPackage { Name = item.Name, Version = item.Version });
            }
        }

        return result;
    }
}
