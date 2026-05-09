using Eryph.GuestServices.Pty;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class PtyForwarder(IShellSelector? selector = null) : IDisposable
{
    private readonly IShellSelector _selector = selector ?? DefaultShellSelector.Instance;
    private readonly Dictionary<string, string> _sessionEnvironment = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private IPty? _pty;
    private int _disposed;
    private int _isRunning;

    private uint _width = 80;
    private uint _height = 25;

    public bool IsRunning => Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1;

    /// <summary>
    /// Records an SSH-sent environment variable that may influence shell
    /// selection. Call before <see cref="StartAsync"/>; values added later are
    /// ignored.
    /// </summary>
    public void SetEnvironmentVariable(string name, string value)
    {
        _sessionEnvironment[name] = value;
    }

    public async Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            throw new InvalidOperationException("Pty instance is already running.");

        var selection = await _selector.SelectAsync(_sessionEnvironment, cancellation);

        _pty = PtyProvider.CreatePty();
        await _pty.StartAsync(_width, _height, selection.Command, selection.Arguments);
        _ = RunAsync(stream, _pty);
    }

    private async Task RunAsync(SshStream stream, IPty pty)
    {
        _ = pty.Output!.CopyToAsync(stream, _cts.Token);
        _ = stream.CopyToAsync(pty.Input!, _cts.Token);

        var result = await pty.WaitForExitAsync(_cts.Token);
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
