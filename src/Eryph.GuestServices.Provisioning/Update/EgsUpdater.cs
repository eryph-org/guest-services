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

            var payloadDir = await StageAsync(decision.TargetVersion!, decision.File!, cancellationToken)
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
        ReleaseFile file,
        CancellationToken cancellationToken)
    {
        var versionDir = Path.Combine(AgentPaths.UpdateDirectory, SanitizeVersion(targetVersion));
        var payloadDir = Path.Combine(versionDir, "payload");
        var zipPath = Path.Combine(versionDir, "package.zip");

        // Start each attempt from a clean slate so a previous half-extraction
        // can't poison this one.
        if (Directory.Exists(versionDir))
            Directory.Delete(versionDir, recursive: true);
        Directory.CreateDirectory(versionDir);

        logger.LogInformation("Self-update: downloading {Url}.", file.Url);
        await DownloadAsync(file.Url!, zipPath, cancellationToken).ConfigureAwait(false);

        var actual = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(file.Sha256Checksum!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Self-update: SHA256 mismatch (expected {Expected}, got {Actual}); aborting.",
                file.Sha256Checksum, actual);
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

    // Defend the staging path against odd version strings (a pinned value comes
    // straight from user-data). Keep only filename-safe characters.
    private static string SanitizeVersion(string version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = version.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
