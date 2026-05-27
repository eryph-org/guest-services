using AwesomeAssertions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

/// <summary>
/// Regression tests for the drain-then-close sequence that runs after the shell
/// process exits. The channel must always be closed regardless of how the PTY
/// output pump terminates — otherwise the SSH client never receives
/// CHANNEL_CLOSE and hangs on logout.
/// </summary>
public class PtyForwarderDrainTests
{
    private static readonly TimeSpan FastBudget = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan FastTimeout = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task ClosesChannel_WhenOutputPumpFaultsWithIoException_AsOnLinuxPtyEio()
    {
        // On Linux the master FD read faults with EIO once the PTY slave closes.
        // That IOException must not skip the channel close (the original bug).
        var outputTask = Task.FromException(new IOException("Input/output error"));
        var closed = false;

        using var drainCts = new CancellationTokenSource();
        await PtyForwarder.DrainAndCloseAsync(
            outputTask,
            drainCts,
            _ => { closed = true; return Task.CompletedTask; },
            CancellationToken.None,
            FastBudget,
            FastTimeout);

        closed.Should().BeTrue();
    }

    [Fact]
    public async Task ClosesChannel_WhenOutputPumpCompletesAtEof()
    {
        var closed = false;

        using var drainCts = new CancellationTokenSource();
        await PtyForwarder.DrainAndCloseAsync(
            Task.CompletedTask,
            drainCts,
            _ => { closed = true; return Task.CompletedTask; },
            CancellationToken.None,
            FastBudget,
            FastTimeout);

        closed.Should().BeTrue();
    }

    [Fact]
    public async Task ClosesChannel_WhenOutputPumpStallsBeyondDrainTimeout()
    {
        // A read that never returns and ignores cancellation must not wedge the
        // close: the drain timeout backstops it.
        var stalled = new TaskCompletionSource();
        var closed = false;

        using var drainCts = new CancellationTokenSource();
        await PtyForwarder.DrainAndCloseAsync(
            stalled.Task,
            drainCts,
            _ => { closed = true; return Task.CompletedTask; },
            CancellationToken.None,
            FastBudget,
            FastTimeout);

        closed.Should().BeTrue();
        stalled.SetResult();
    }

    [Fact]
    public async Task DoesNotCloseChannel_WhenSessionIsAlreadyShuttingDown()
    {
        // A cancelled session token means teardown is underway; closing the
        // channel is pointless and would itself throw.
        var closed = false;
        using var shutdownCts = new CancellationTokenSource();
        await shutdownCts.CancelAsync();

        using var drainCts = new CancellationTokenSource();
        await PtyForwarder.DrainAndCloseAsync(
            new TaskCompletionSource().Task,
            drainCts,
            _ => { closed = true; return Task.CompletedTask; },
            shutdownCts.Token,
            FastBudget,
            FastTimeout);

        closed.Should().BeFalse();
    }
}
