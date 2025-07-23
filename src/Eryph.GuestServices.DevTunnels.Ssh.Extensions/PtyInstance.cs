using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.XPath;
using Eryph.GuestServices.Pty;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public sealed class PtyInstance : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPty? _pty;

    private uint _width = 80;
    private uint _height = 25;

    public async Task<int> RunAsync(SshStream stream, CancellationToken cancellation)
    {
        _pty = PtyProvider.CreatePty();
        var command = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash";
        await _pty.StartAsync(_width, _height, command);

        _ = _pty.Output.CopyToAsync(stream, cancellation);
        _ = stream.CopyToAsync(_pty.Input, cancellation);

        var result = await _pty.WaitForExitAsync(cancellation);
        return result;
    }

    public async Task ResizeAsync(
        uint width,
        uint height,
        CancellationToken cancellation)
    {
        await _lock.WaitAsync(cancellation);
        try
        {
            _width = width;
            _height = height;
            if (_pty is not null)
                await _pty.ResizeAsync(width, height);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _pty?.Dispose();
        _lock.Dispose();
    }
}
