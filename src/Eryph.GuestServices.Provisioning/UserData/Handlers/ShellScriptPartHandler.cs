using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData.Handlers;

internal sealed class ShellScriptPartHandler(ILogger<ShellScriptPartHandler> logger) : IUserDataHandler
{
    public bool CanHandle(UserDataPart part) =>
        part.ContentType.Equals(UserDataContentTypeSniffer.ShellScript, StringComparison.OrdinalIgnoreCase);

    public Task ProcessAsync(UserDataPart part, IUserDataResolutionContext ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var kind = UserDataContentTypeSniffer.DetectScriptKind(part.Body);
        ctx.AddScript(new ScriptPayload(kind, part.Body, part.Filename));
        logger.LogDebug(
            "Captured shell script '{Filename}' ({Kind}, {Bytes} bytes)",
            part.Filename ?? "<root>",
            kind,
            part.Body.Length);
        return Task.CompletedTask;
    }
}
