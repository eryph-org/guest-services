using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.UserData.Handlers;

internal sealed class ShellScriptPartHandler(ILogger<ShellScriptPartHandler> logger) : IUserDataHandler
{
    public bool CanHandle(UserDataPart part) =>
        part.ContentType.Equals(UserDataContentTypeSniffer.ShellScript, StringComparison.OrdinalIgnoreCase);

    public Task ProcessAsync(UserDataPart part, IUserDataResolutionContext ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Filename-led detection — see ScriptKindDetector / RFC 0007. The
        // detector logs a warning whenever it cannot confidently classify
        // (or has to fall back on content-type), so the handler stays quiet.
        var kind = ScriptKindDetector.Detect(part.Filename, part.Body, part.ContentType, logger);
        ctx.AddScript(new ScriptPayload(kind, part.Body, part.Filename));
        logger.LogDebug(
            "Captured shell script '{Filename}' ({Kind}, {Bytes} bytes)",
            part.Filename ?? "<root>",
            kind,
            part.Body.Length);
        return Task.CompletedTask;
    }
}
