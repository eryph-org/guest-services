using Eryph.GuestServices.Provisioning.Reporting.Events;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Reporting;

// Fans events out to every registered handler. Handler failures are isolated:
// a throwing handler is logged and skipped so one broken sink cannot derail
// provisioning.
internal sealed class ReportingDispatcher(
    IEnumerable<IReportingHandler> handlers,
    ILogger<ReportingDispatcher> logger) : IReportingDispatcher
{
    private readonly IReportingHandler[] _handlers = handlers.ToArray();

    public async Task EmitAsync(ReportingEvent reportingEvent, CancellationToken cancellationToken)
    {
        foreach (var handler in _handlers)
        {
            if (!handler.IsApplicable)
                continue;

            try
            {
                await handler.PublishAsync(reportingEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Reporting handler {Handler} threw while publishing {Event}; continuing",
                    handler.GetType().Name,
                    reportingEvent.GetType().Name);
            }
        }
    }
}
