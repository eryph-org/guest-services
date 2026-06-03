using Eryph.ComputeClient;
using Eryph.ComputeClient.Models;

namespace Eryph.GuestServices.Tool.Eryph;

// Shared operation polling for the eryph commands. eryph's API never blocks on
// an operation, so the client starts it and polls until it terminates.
internal static class EryphOperations
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    // Polls until the operation reaches a terminal state; returns it (Completed or
    // Failed), or null on timeout. Transient poll failures are retried.
    public static async Task<Operation?> WaitForCompletionAsync(OperationsClient operations, string operationId)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            Operation operation;
            try
            {
                operation = (await operations.GetAsync(operationId)).Value;
            }
            catch (Exception)
            {
                // Resilient poll: a transient network/auth blip — or an HTTP request
                // timeout, which surfaces as TaskCanceledException (an
                // OperationCanceledException) — must not abort the wait. There is no
                // external cancellation token here, so retry until the deadline.
                await Task.Delay(Interval);
                continue;
            }

            if (operation.Status == OperationStatus.Completed || operation.Status == OperationStatus.Failed)
                return operation;

            await Task.Delay(Interval);
        }

        return null;
    }
}
