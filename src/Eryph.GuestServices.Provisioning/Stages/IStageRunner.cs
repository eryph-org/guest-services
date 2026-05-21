namespace Eryph.GuestServices.Provisioning.Stages;

public interface IStageRunner
{
    Task<StageRunOutcome> RunAsync(CancellationToken cancellationToken);
}
