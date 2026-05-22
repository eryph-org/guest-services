using System.IO.Compression;
using System.Text;
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

        foreach (var entry in config.WriteFiles)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                logger.LogWarning("Skipping write_files entry with empty path.");
                continue;
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
                continue;
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

            if (!string.IsNullOrWhiteSpace(entry.Permissions))
                logger.LogWarning(
                    "File permissions '{Perms}' on '{Path}' are ignored on Windows beyond basic ACLs.",
                    entry.Permissions, windowsPath);

            if (!string.IsNullOrWhiteSpace(entry.Owner))
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
        }

        return ModuleOutcome.Ok();
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
