using Eryph.GuestServices.Provisioning.Semaphores;

namespace Eryph.GuestServices.Provisioning.Tests.Semaphores;

/// <summary>
/// Test double for <see cref="IBootSessionDetector"/>. Default behaviour
/// is to claim a new boot exactly once; subsequent calls return whatever
/// <see cref="NextResult"/> is set to. Tests can drive it explicitly for
/// scenarios that simulate multi-boot timelines.
/// </summary>
internal sealed class StubBootSessionDetector : IBootSessionDetector
{
    public bool NextResult { get; set; } = true;

    public int CallCount { get; private set; }

    public Task<bool> IsNewBootAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        var result = NextResult;
        // After the first call, default to "same boot" — matches real-world
        // observation: detector reports new boot once per agent startup at
        // most.
        NextResult = false;
        return Task.FromResult(result);
    }
}
