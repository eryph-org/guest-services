using System.IO.Compression;
using System.Text;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

[Stage(Stage.Config, Order = 3, Frequency = ModuleFrequency.PerInstance)]
internal sealed class WriteFilesModule(ILogger<WriteFilesModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;
        if (config.WriteFiles is null || config.WriteFiles.Count == 0)
            return ModuleOutcome.Ok();

        // Cloud-init contract: entries with `defer: true` are postponed to
        // the Final stage so they can reference users (or other state) that
        // earlier Config-stage modules created. WriteFilesDeferredModule
        // handles those; this module processes only the non-deferred entries.
        foreach (var entry in config.WriteFiles)
        {
            if (entry.Defer == true)
                continue;

            var outcome = await WriteFilesProcessor.ProcessEntryAsync(
                entry, context, logger, cancellationToken).ConfigureAwait(false);
            if (outcome is { } fail)
                return fail;
        }

        return ModuleOutcome.Ok();
    }
}

/// <summary>
/// Final-stage counterpart to <see cref="WriteFilesModule"/>. Picks up the
/// <c>write_files</c> entries marked <c>defer: true</c> and applies them
/// after users / groups / passwords have been processed. Cloud-init's
/// equivalent runs in <c>cc_write_files_deferred.py</c> at the same place
/// in the run.
/// </summary>
[Stage(Stage.Final, Order = -1, Frequency = ModuleFrequency.PerInstance)]
internal sealed class WriteFilesDeferredModule(ILogger<WriteFilesDeferredModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;
        if (config.WriteFiles is null || config.WriteFiles.Count == 0)
            return ModuleOutcome.Ok();

        foreach (var entry in config.WriteFiles)
        {
            if (entry.Defer != true)
                continue;

            var outcome = await WriteFilesProcessor.ProcessEntryAsync(
                entry, context, logger, cancellationToken).ConfigureAwait(false);
            if (outcome is { } fail)
                return fail;
        }

        return ModuleOutcome.Ok();
    }
}

/// <summary>
/// Shared per-entry writer used by both <see cref="WriteFilesModule"/>
/// (Config stage, non-deferred entries) and
/// <see cref="WriteFilesDeferredModule"/> (Final stage, deferred entries).
/// Returns <c>null</c> on success or a <see cref="ModuleOutcome.Failed"/>
/// when the entry must abort the module (path traversal); other failures
/// (encoding, ACL) log and continue.
/// </summary>
internal static class WriteFilesProcessor
{
    public static async Task<ModuleOutcome?> ProcessEntryAsync(
        WriteFileConfig entry,
        IModuleContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.Path))
        {
            logger.LogWarning("Skipping write_files entry with empty path.");
            return null;
        }

        byte[] content;
        try
        {
            content = DecodeContent(entry.Content, entry.Encoding);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to decode content for '{Path}' with encoding '{Encoding}'; skipping.",
                entry.Path, entry.Encoding ?? "<none>");
            return null;
        }

        string windowsPath;
        try
        {
            windowsPath = context.Os.TranslateUnixPath(entry.Path);
        }
        catch (ArgumentException ex)
        {
            // TranslateUnixPath rejects ".." segments and paths that escape
            // the C:\ root after canonicalization. We surface that as a
            // module failure rather than continuing — silently skipping
            // hostile input would hide configuration errors.
            logger.LogError(ex, "Rejecting write_files entry with unsafe path '{Path}'.", entry.Path);
            return ModuleOutcome.Fail($"path traversal: {entry.Path}");
        }

        var parent = Path.GetDirectoryName(windowsPath);
        if (!string.IsNullOrEmpty(parent))
            await context.Os.EnsureDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);

        var append = entry.Append == true;
        logger.LogInformation(
            "Writing {Bytes} byte(s) to '{Path}' (append={Append}).",
            content.Length, windowsPath, append);
        await context.Os.WriteFileAsync(windowsPath, content, append, cancellationToken).ConfigureAwait(false);

        // When `permissions` is set, route through SetPosixPermissionsAsync —
        // it does the POSIX-mode → NTFS-ACL translation (commit 0635c5c) AND
        // sets the owner if provided. When ONLY `owner` is set, we want to
        // leave the existing ACL untouched, so we use the narrower
        // SetFileOwnerAsync.
        if (!string.IsNullOrWhiteSpace(entry.Permissions))
        {
            logger.LogInformation(
                "Applying POSIX permissions '{Perms}' (owner '{Owner}') to '{Path}'.",
                entry.Permissions, entry.Owner ?? "<unchanged>", windowsPath);
            try
            {
                await context.Os.SetPosixPermissionsAsync(
                    windowsPath, entry.Permissions, entry.Owner, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to apply permissions '{Perms}' on '{Path}'.",
                    entry.Permissions, windowsPath);
            }
        }
        else if (!string.IsNullOrWhiteSpace(entry.Owner))
        {
            logger.LogInformation("Setting owner of '{Path}' to '{Owner}'.", windowsPath, entry.Owner);
            try
            {
                await context.Os.SetFileOwnerAsync(windowsPath, entry.Owner, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to set owner '{Owner}' on '{Path}'.", entry.Owner, windowsPath);
            }
        }

        return null;
    }

    private static byte[] DecodeContent(string? content, string? encoding)
    {
        var text = content ?? string.Empty;
        var normalized = (encoding ?? "").Trim().ToLowerInvariant();

        return normalized switch
        {
            "" or "text/plain" =>
                Encoding.UTF8.GetBytes(text),
            "b64" or "base64" =>
                Convert.FromBase64String(text),
            "gz" or "gzip" =>
                Gunzip(Encoding.UTF8.GetBytes(text)),
            "gz+b64" or "gzip+b64" or "gz+base64" or "gzip+base64" or "b64+gzip" or "base64+gzip" =>
                Gunzip(Convert.FromBase64String(text)),
            _ => throw new NotSupportedException($"Unsupported write_files encoding '{encoding}'."),
        };
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
