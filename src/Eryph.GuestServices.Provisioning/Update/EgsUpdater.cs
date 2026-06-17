using System.IO.Compression;
using System.Security.Cryptography;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Core;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>
/// Default <see cref="IEgsUpdater"/>: fetches the release index over HTTPS,
/// resolves the target via <see cref="UpdateTargetResolver"/>, downloads and
/// SHA256-verifies the Windows package, and extracts it into a per-version
/// staging directory under <see cref="AgentPaths.UpdateDirectory"/>.
/// </summary>
public sealed class EgsUpdater(
    HttpClient httpClient,
    IAgentVersionProvider versionProvider,
    IReleaseSignatureVerifier signatureVerifier,
    ILogger<EgsUpdater> logger) : IEgsUpdater
{
    public const string DefaultIndexUrl = "https://releases.dbosoft.eu/eryph/guest-services/index.json";

    /// <summary>The agent executable expected inside a valid payload.</summary>
    private const string AgentExecutable = "egs-service.exe";

    public async Task<UpdatePlan?> PrepareAsync(EgsUpdateConfig? config, CancellationToken cancellationToken)
    {
        if (config is null || config.Enabled != true)
        {
            logger.LogDebug("egs.update not enabled; skipping self-update.");
            return null;
        }

        try
        {
            var current = versionProvider.GetCurrentVersion();
            var index = await FetchIndexAsync(cancellationToken).ConfigureAwait(false);
            var decision = UpdateTargetResolver.Resolve(index, config, current);
            if (!decision.ShouldUpdate)
            {
                logger.LogInformation("Self-update: no action ({Reason}).", decision.Reason);
                return null;
            }

            logger.LogInformation(
                "Self-update: {Reason} (running {Current}).", decision.Reason, current);

            var release = index.Versions![decision.TargetVersion!];
            var payloadDir = await StageAsync(decision.TargetVersion!, release, decision.File!, cancellationToken)
                .ConfigureAwait(false);
            if (payloadDir is null)
                return null;

            return new UpdatePlan
            {
                StagingDirectory = payloadDir,
                TargetVersion = decision.TargetVersion!,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed update preparation must never fail provisioning — log and
            // continue (the rest of the egs block / later stages still run).
            logger.LogWarning(ex, "Self-update preparation failed; continuing without updating.");
            return null;
        }
    }

    private async Task<ReleaseIndex> FetchIndexAsync(CancellationToken cancellationToken)
    {
        var json = await httpClient.GetStringAsync(DefaultIndexUrl, cancellationToken).ConfigureAwait(false);
        return ReleaseIndex.Parse(json);
    }

    /// <summary>
    /// Downloads + verifies + extracts the package. Returns the payload
    /// directory, or null when the download/verify/extract failed (logged).
    /// </summary>
    private async Task<string?> StageAsync(
        string targetVersion,
        ReleaseVersion release,
        ReleaseFile file,
        CancellationToken cancellationToken)
    {
        var versionDir = StagingDirFor(targetVersion);
        var payloadDir = Path.Combine(versionDir, "payload");
        var zipPath = Path.Combine(versionDir, "package.zip");

        // Start each attempt from a clean slate so a previous half-extraction
        // can't poison this one.
        if (Directory.Exists(versionDir))
            Directory.Delete(versionDir, recursive: true);
        Directory.CreateDirectory(versionDir);

        // Establish trust FIRST: fetch the small SHA256SUMS + signature, verify
        // the OpenPGP signature (the index itself is only HTTPS, not signed),
        // and read the authoritative package hash. Only then download the
        // (large) package — so a tampered index can't make us pull a huge file
        // before any trust is established.
        var signedHash = await VerifySignedHashAsync(release, file, cancellationToken).ConfigureAwait(false);
        if (signedHash is null)
            return null; // signature missing/invalid or file absent from SUMS (logged)

        logger.LogInformation("Self-update: downloading {Url}.", file.Url);
        await DownloadAsync(file.Url!, zipPath, cancellationToken).ConfigureAwait(false);

        var actual = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(signedHash, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Self-update: SHA256 mismatch against signed checksums (expected {Expected}, got {Actual}); aborting.",
                signedHash, actual);
            return null;
        }

        Directory.CreateDirectory(payloadDir);
        ZipFile.ExtractToDirectory(zipPath, payloadDir, overwriteFiles: true);

        var agentPath = FindAgentExecutable(payloadDir);
        if (agentPath is null)
        {
            logger.LogError(
                "Self-update: extracted payload does not contain {Exe}; aborting.", AgentExecutable);
            return null;
        }

        logger.LogInformation(
            "Self-update: staged {Version} at {Dir} (verified).", targetVersion, agentPath);
        return Path.GetDirectoryName(agentPath)!;
    }

    /// <summary>
    /// Downloads the signed <c>SHA256SUMS</c> + its detached signature
    /// (siblings of the package URL), verifies the signature with a bundled
    /// dbosoft key, and returns the signed hash for the package — or null when
    /// the signature is missing/invalid or the file isn't listed (fail closed:
    /// an unsigned/untrusted payload is never applied).
    /// </summary>
    private async Task<string?> VerifySignedHashAsync(
        ReleaseVersion release,
        ReleaseFile file,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(release.ChecksumsFile)
            || string.IsNullOrWhiteSpace(release.ChecksumsSignatureFile))
        {
            logger.LogError("Self-update: release has no signed SHA256SUMS; refusing to update unsigned.");
            return null;
        }

        var baseUrl = BaseUrl(file.Url!);
        var sumsBytes = await DownloadBytesAsync(baseUrl + release.ChecksumsFile, cancellationToken).ConfigureAwait(false);
        var sigBytes = await DownloadBytesAsync(baseUrl + release.ChecksumsSignatureFile, cancellationToken).ConfigureAwait(false);

        if (!signatureVerifier.Verify(sumsBytes, sigBytes))
        {
            logger.LogError("Self-update: SHA256SUMS signature did not verify against the dbosoft keys; aborting.");
            return null;
        }

        var sums = Sha256Sums.Parse(System.Text.Encoding.UTF8.GetString(sumsBytes));
        var hash = sums.GetHash(file.Filename!);
        if (hash is null)
            logger.LogError("Self-update: {File} is not listed in the signed SHA256SUMS; aborting.", file.Filename);
        return hash;
    }

    // The SUMS and signature sit next to the package in the same version dir.
    private static string BaseUrl(string packageUrl)
    {
        var slash = packageUrl.LastIndexOf('/');
        return slash >= 0 ? packageUrl[..(slash + 1)] : packageUrl;
    }

    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // The zip may place binaries at the root or under a single top-level folder
    // (e.g. bin/). Search so either layout yields the agent dir to copy from.
    private static string? FindAgentExecutable(string payloadDir)
    {
        var direct = Path.Combine(payloadDir, AgentExecutable);
        if (File.Exists(direct))
            return direct;

        return Directory
            .EnumerateFiles(payloadDir, AgentExecutable, SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    /// <summary>
    /// Resolves the per-version staging directory, defending against a hostile
    /// pinned <c>version</c> from user-data. Invalid filename chars (including
    /// path separators) are neutralised, the relative-traversal tokens
    /// <c>.</c>/<c>..</c> are rejected outright, and the final path is asserted
    /// to be a real child of <see cref="AgentPaths.UpdateDirectory"/> — so a
    /// crafted value can never make the later <c>Directory.Delete</c> escape the
    /// update root and wipe the service's state/logs.
    /// </summary>
    internal static string StagingDirFor(string version)
    {
        var sanitized = SanitizeVersion(version);
        if (sanitized is "." or ".." || sanitized.Length == 0)
            throw new ArgumentException($"Refusing unsafe update version '{version}'.", nameof(version));

        var root = Path.GetFullPath(AgentPaths.UpdateDirectory);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, sanitized));
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Refusing unsafe update version '{version}'.", nameof(version));

        return full;
    }

    // Keep only filename-safe characters (path separators become '_').
    private static string SanitizeVersion(string version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = version.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
