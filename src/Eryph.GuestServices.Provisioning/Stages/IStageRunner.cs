namespace Eryph.GuestServices.Provisioning.Stages;

public interface IStageRunner
{
    Task<StageRunOutcome> RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs only the requested stage (plus the mandatory data-source discovery
    /// pre-step). Other stages are skipped entirely. Useful for the CLI
    /// <c>run --stage</c> option so operators can re-run a single stage in
    /// isolation.
    /// </summary>
    Task<StageRunOutcome> RunStageAsync(Stage stage, CancellationToken cancellationToken);
}
