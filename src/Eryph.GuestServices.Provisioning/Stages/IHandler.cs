namespace Eryph.GuestServices.Provisioning.Stages;

public interface IHandler
{
    Task<HandlerOutcome> ApplyAsync(
        global::Eryph.GuestServices.CloudConfig.CloudConfig config,
        IHandlerContext context,
        CancellationToken cancellationToken);
}
