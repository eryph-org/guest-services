using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData.Handlers;

// v1 captures boothooks but does NOT execute them; execution is RFC-0015.
internal sealed class BoothookPartHandler(ILogger<BoothookPartHandler> logger) : IUserDataHandler
{
    public bool CanHandle(UserDataPart part) =>
        part.ContentType.Equals(UserDataContentTypeSniffer.Boothook, StringComparison.OrdinalIgnoreCase);

    public Task ProcessAsync(UserDataPart part, IUserDataResolutionContext ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ctx.AddBoothook(new BoothookPayload(part.Body, part.Filename));
        logger.LogDebug(
            "Captured cloud-boothook '{Filename}' ({Bytes} bytes)",
            part.Filename ?? "<root>",
            part.Body.Length);
        return Task.CompletedTask;
    }
}
