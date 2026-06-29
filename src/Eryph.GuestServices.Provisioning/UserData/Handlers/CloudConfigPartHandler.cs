using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Serialization;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData.Handlers;

internal sealed class CloudConfigPartHandler(
    ICloudConfigSerializer serializer,
    ILogger<CloudConfigPartHandler> logger) : IUserDataHandler
{
    public bool CanHandle(UserDataPart part) =>
        part.ContentType.Equals(UserDataContentTypeSniffer.CloudConfig, StringComparison.OrdinalIgnoreCase)
        || part.ContentType.Equals("text/cloud-config", StringComparison.OrdinalIgnoreCase);

    public Task ProcessAsync(UserDataPart part, IUserDataResolutionContext ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // BOM-stripping decode — Set-Content -Encoding UTF8 in Windows
        // PowerShell writes a leading EF BB BF, and YamlDotNet rejects the
        // resulting U+FEFF before #cloud-config.
        var yaml = UserDataEncoding.DecodeUtf8(part.Body);
        try
        {
            var fragment = serializer.Deserialize(yaml);
            // cloud-init merge_how / merge_type (RFC 0032): this fragment's
            // directive controls how it merges onto the accumulated config.
            var options = serializer.ReadMergeOptions(yaml) ?? CloudInitMergeOptions.CloudInitDefault;
            ctx.MergeCloudConfig(fragment, options);
            logger.LogDebug("Merged cloud-config fragment from {Filename}", part.Filename ?? "<root>");
        }
        catch (Exception ex)
        {
            // Bubble up: a malformed cloud-config is a hard failure for that
            // fragment. StageRunner converts the pipeline exception into a
            // ProvisioningFailed report.
            logger.LogError(ex, "Failed to parse cloud-config fragment '{Filename}'", part.Filename ?? "<root>");
            throw;
        }
        return Task.CompletedTask;
    }
}
