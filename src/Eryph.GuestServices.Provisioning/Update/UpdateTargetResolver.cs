using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>
/// Pure decision logic for the self-updater: given the release index, the
/// operator's <c>egs.update</c> directive, and the running version, decide
/// whether to update and to which release artifact. No I/O — so the
/// channel/pin/already-current rules are exhaustively unit-testable.
/// </summary>
public static class UpdateTargetResolver
{
    public const string StableChannel = "stable";
    public const string UnstableChannel = "unstable";

    public static UpdateDecision Resolve(
        ReleaseIndex index,
        EgsUpdateConfig? config,
        string currentVersion)
    {
        // Opt-in only: no update unless explicitly enabled.
        if (config is null || config.Enabled != true)
            return UpdateDecision.None("egs.update not enabled");

        var target = ResolveTargetVersion(index, config);
        if (string.IsNullOrWhiteSpace(target))
            return UpdateDecision.None("no target version could be resolved from the index");

        // A target equal to the running version is a no-op — equality only, so
        // an explicit downgrade pin is still honoured (target != current ⇒ act).
        if (VersionsEqual(target, currentVersion))
            return UpdateDecision.None($"already running target version {target}");

        if (index.Versions is null || !index.Versions.TryGetValue(target, out var release) || release.Files is null)
            return UpdateDecision.None($"target version {target} is not present in the index");

        var file = SelectWindowsPackage(release.Files);
        if (file is null)
            return UpdateDecision.None($"target version {target} has no windows/amd64 package");

        if (string.IsNullOrWhiteSpace(file.Url) || string.IsNullOrWhiteSpace(file.Sha256Checksum))
            return UpdateDecision.None($"target version {target} package is missing a url or checksum");

        return UpdateDecision.Update(target, file, $"update to {target}");
    }

    private static string? ResolveTargetVersion(ReleaseIndex index, EgsUpdateConfig config)
    {
        // A pinned version wins over the channel.
        if (!string.IsNullOrWhiteSpace(config.Version))
            return config.Version!.Trim();

        var channel = string.IsNullOrWhiteSpace(config.Channel)
            ? StableChannel
            : config.Channel!.Trim();

        return channel.Equals(UnstableChannel, StringComparison.OrdinalIgnoreCase)
            ? index.LatestVersion
            // Anything that isn't explicitly "unstable" is treated as stable —
            // an unknown channel string must never silently jump to a preview.
            : index.LatestStableVersion;
    }

    /// <summary>
    /// Picks the Windows x64 <em>service</em> archive: os=windows, arch=amd64, a
    /// <c>.zip</c>, not the ISO. Crucially this excludes the CLI-tool package
    /// (<c>egs-tool_…_windows_amd64.zip</c>) — which is also windows/amd64/.zip
    /// but does NOT contain <c>egs-service.exe</c> — and prefers the service
    /// archive (<c>egs_…_windows_amd64.zip</c>).
    /// </summary>
    internal static ReleaseFile? SelectWindowsPackage(IReadOnlyList<ReleaseFile> files)
    {
        var candidates = files.Where(f =>
                string.Equals(f.Os, "windows", StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.Arch, "amd64", StringComparison.OrdinalIgnoreCase)
                && f.Filename is not null
                && f.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !f.Filename.StartsWith("egs-tool", StringComparison.OrdinalIgnoreCase)
                && (f.Tags is null || !f.Tags.Any(t => string.Equals(t, "iso", StringComparison.OrdinalIgnoreCase))))
            .ToList();

        // Prefer the canonical service archive name; otherwise the first
        // non-tool windows zip.
        return candidates.FirstOrDefault(f =>
                   f.Filename!.StartsWith("egs_", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    // Compare the SemVer core+prerelease, ignoring build metadata (everything
    // after '+'). The running InformationalVersion carries a "+Branch.x.Sha.y"
    // suffix the index version keys never have.
    internal static bool VersionsEqual(string a, string b) =>
        string.Equals(StripBuildMetadata(a), StripBuildMetadata(b), StringComparison.OrdinalIgnoreCase);

    internal static string StripBuildMetadata(string version)
    {
        var v = version.Trim();
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}

/// <summary>Outcome of <see cref="UpdateTargetResolver.Resolve"/>.</summary>
public sealed record UpdateDecision
{
    public bool ShouldUpdate { get; private init; }

    public string? TargetVersion { get; private init; }

    public ReleaseFile? File { get; private init; }

    public string Reason { get; private init; } = "";

    public static UpdateDecision None(string reason) => new() { ShouldUpdate = false, Reason = reason };

    public static UpdateDecision Update(string targetVersion, ReleaseFile file, string reason) =>
        new() { ShouldUpdate = true, TargetVersion = targetVersion, File = file, Reason = reason };
}
