using System.Collections.Frozen;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Pty;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class PtyForwarder(IShellSelector? selector = null) : IDisposable
{
    private readonly IShellSelector _selector = selector ?? DefaultShellSelector.Instance;
    private readonly Dictionary<string, string> _sessionEnvironment = new(StringComparer.Ordinal);
    private readonly Lock _envLock = new();
    private readonly CancellationTokenSource _cts = new();

    private IPty? _pty;
    private int _disposed;
    private int _isRunning;

    private uint _width = 80;
    private uint _height = 25;

    public bool IsRunning => Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1;

    /// <summary>
    /// Records an SSH-sent environment variable that may influence shell
    /// selection. Only the keys this forwarder actually consults are kept;
    /// arbitrary names are dropped to avoid unbounded growth from a hostile
    /// client. Calls after <see cref="StartAsync"/> are ignored — by then the
    /// selector has already snapshotted the dictionary.
    /// </summary>
    public void SetEnvironmentVariable(string name, string value)
    {
        if (IsRunning)
            return;
        if (name != Constants.ShellEnvName && name != Constants.ShellArgsEnvName)
            return;

        lock (_envLock)
        {
            _sessionEnvironment[name] = value;
        }
    }

    public async Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            throw new InvalidOperationException("Pty instance is already running.");

        // Snapshot the env dict while no further writes can land (IsRunning is
        // now true, SetEnvironmentVariable early-returns) and hand the
        // selector an immutable view.
        FrozenDictionary<string, string> envSnapshot;
        lock (_envLock)
        {
            envSnapshot = _sessionEnvironment.ToFrozenDictionary(StringComparer.Ordinal);
        }
        var selection = await _selector.SelectAsync(envSnapshot, cancellation);

        _pty = PtyProvider.CreatePty();
        await _pty.StartAsync(_width, _height, selection.Command, selection.Arguments);
        _ = RunAsync(stream, _pty);
    }

    private async Task RunAsync(SshStream stream, IPty pty)
    {
        // Give the output pump its own cancellation source so we can stop it
        // before closing the channel without tearing down the rest of the
        // forwarder.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var outputTask = pty.Output!.CopyToAsync(stream, pumpCts.Token);
        _ = stream.CopyToAsync(pty.Input!, _cts.Token);

        var result = await pty.WaitForExitAsync(_cts.Token);

        // Brief natural-drain window for any final bytes the PTY emitted on
        // shell exit (e.g. ConPTY's terminal-reset escape sequence after
        // `cmd.exe` exits). The PTY master never returns EOF while we hold
        // its handle, so this can only time out — the timeout is the budget.
        try
        {
            await outputTask.WaitAsync(TimeSpan.FromMilliseconds(500), _cts.Token);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }

        // Stop the pump and wait for it to actually finish before closing.
        // Otherwise an in-flight read could deliver bytes *after* the
        // channel-close message goes out, and the client would log
        // "data packet referred to nonexistent channel".
        await pumpCts.CancelAsync();
        try
        {
            await outputTask;
        }
        catch (Exception) { /* expected on cancellation / pipe disposal */ }

        // Flush any data still sitting in the SshStream's write queue so the
        // bytes hit the wire before the channel-close message does.
        try
        {
            await stream.FlushAsync(_cts.Token);
        }
        catch (Exception) { /* best-effort drain */ }

        await stream.Channel.CloseAsync(unchecked((uint)result), _cts.Token);
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

        _cts.Dispose();
        _pty?.Dispose();
    }
}
