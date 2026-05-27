using Eryph.GuestServices.Core;
using Eryph.GuestServices.Pty;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class PtyForwarder(IShellSelector? selector = null) : IDisposable
{
    private readonly IShellSelector _selector = selector ?? DefaultShellSelector.Instance;
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _cts = new();

    private string? _shellOverride;
    private string? _shellArgsOverride;
    private bool _isRunning;

    private IPty? _pty;
    private int _disposed;

    private uint _width = 80;
    private uint _height = 25;

    public bool IsRunning
    {
        get { lock (_stateLock) { return _isRunning; } }
    }

    /// <summary>
    /// Records an SSH-sent environment variable that may influence shell
    /// selection. Returns <see langword="true"/> if the value was accepted
    /// (recognized name, forwarder not yet started). Unknown names and
    /// late writes are rejected so the SSH-level response can stay honest.
    /// </summary>
    public bool TrySetEnvironmentVariable(string name, string value)
    {
        lock (_stateLock)
        {
            if (_isRunning)
                return false;

            if (name == Constants.ShellEnvName)
            {
                _shellOverride = value;
                return true;
            }
            if (name == Constants.ShellArgsEnvName)
            {
                _shellArgsOverride = value;
                return true;
            }
            return false;
        }
    }

    public async Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        // Atomically flip to running and snapshot the override under one lock
        // so a concurrent TrySetEnvironmentVariable cannot land between the
        // flag flip and the snapshot.
        ShellOverride snapshot;
        lock (_stateLock)
        {
            if (_isRunning)
                throw new InvalidOperationException("Pty instance is already running.");
            _isRunning = true;
            snapshot = new ShellOverride(_shellOverride, _shellArgsOverride);
        }

        var selection = await _selector.SelectAsync(snapshot, cancellation);

        _pty = PtyProvider.CreatePty();
        await _pty.StartAsync(_width, _height, selection.Command, selection.Arguments);
        _ = RunAsync(stream, _pty);
    }

    private async Task RunAsync(SshStream stream, IPty pty)
    {
        using var outputDrainCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var outputTask = pty.Output!.CopyToAsync(stream, outputDrainCts.Token);
        _ = stream.CopyToAsync(pty.Input!, _cts.Token);

        var result = await pty.WaitForExitAsync(_cts.Token);

        await DrainAndCloseAsync(
            outputTask,
            outputDrainCts,
            ct => stream.Channel.CloseAsync(unchecked((uint)result), ct),
            _cts.Token);
    }

    /// <summary>
    /// Drains any trailing PTY output, then closes the channel — and closes it
    /// no matter how the output pump ends, short of a session shutdown.
    /// </summary>
    /// <remarks>
    /// ConPTY emits final reset sequences asynchronously after the child exits,
    /// so we give them a moment to drain before CHANNEL_CLOSE; otherwise they
    /// ship as CHANNEL_DATA after close and strict SSH clients (OpenSSH)
    /// disconnect. But the pump always ends abnormally on teardown — at EOF on
    /// Windows, with an EIO <see cref="IOException"/> on Linux once the PTY
    /// slave closes — and an in-flight <see cref="FileStream"/> read can ignore
    /// cancellation. So the drain must neither block the close indefinitely nor
    /// let the pump's terminal exception propagate: <see cref="RunAsync"/> is
    /// fire-and-forget, so a thrown exception here would skip the close and the
    /// client would never receive CHANNEL_CLOSE — hanging the session on logout.
    /// </remarks>
    internal static async Task DrainAndCloseAsync(
        Task outputTask,
        CancellationTokenSource outputDrainCts,
        Func<CancellationToken, Task> closeAsync,
        CancellationToken cancellation,
        TimeSpan? drainBudget = null,
        TimeSpan? drainTimeout = null)
    {
        outputDrainCts.CancelAfter(drainBudget ?? TimeSpan.FromSeconds(1));
        try
        {
            await outputTask.WaitAsync(drainTimeout ?? TimeSpan.FromSeconds(2), cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The session/forwarder is shutting down; closing is pointless.
            return;
        }
        catch (TimeoutException)
        {
            // The read ignored cancellation; Dispose will tear down the PTY and
            // unblock it. Observe the eventual fault so it isn't unobserved.
            _ = outputTask.ContinueWith(static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        catch
        {
            // Pump ended via EOF, EIO, or the drain-budget cancellation
            // (outputDrainCts) — all expected; close the channel regardless.
        }

        await closeAsync(cancellation);
    }

    public async Task ResizeAsync(
        uint width,
        uint height,
        CancellationToken cancellation)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            return;

        _width = width;
        _height = height;
        if (_pty is not null)
            await _pty.ResizeAsync(width, height);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Cancel before disposing so both copy pumps observe cancellation and stop
        // instead of lingering until the underlying pipes happen to close.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        _pty?.Dispose();
    }
}
