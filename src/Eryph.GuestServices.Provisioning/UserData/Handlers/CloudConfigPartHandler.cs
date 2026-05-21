using System.Text;
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

        var yaml = Encoding.UTF8.GetString(part.Body);
        try
        {
            var fragment = serializer.Deserialize(yaml);
            ctx.MergeCloudConfig(fragment);
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
