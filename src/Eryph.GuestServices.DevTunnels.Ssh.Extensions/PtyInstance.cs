using Eryph.GuestServices.Pty;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public sealed class PtyInstance : IDisposable
{
    private IPty? _pty;
    private readonly CancellationTokenSource _cts = new();
    
    private int _disposed;
    private int _isRunning;

    private uint _width = 80;
    private uint _height = 25;

    public bool IsRunning => Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1;

    public async Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            throw new InvalidOperationException("Pty instance is already running.");

        _pty = PtyProvider.CreatePty();
        var command = "powershell.exe";
        var arguments = "-WindowStyle Hidden";
        if (OperatingSystem.IsLinux())
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            command = string.IsNullOrEmpty(shell) ? "/bin/bash" : shell;
            arguments = "-i";
        }
        
        await _pty.StartAsync(_width, _height, command, arguments);
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
