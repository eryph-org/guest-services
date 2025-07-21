using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Pty.WindowsFallback;

internal sealed class WindowsFallbackPty : IPty, IDisposable
{
    private Process? _process;

    public Stream Input => _process!.StandardInput.BaseStream;

    public Stream Output => _process!.StandardOutput.BaseStream;

    public Task StartAsync(uint width, uint height, string command)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh-shellhost.exe",
                Arguments = "---pty powershell.exe -WindowStyle Hidden",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        await _process!.WaitForExitAsync(cancellation);
        return _process.ExitCode;
    }

    public Task ResizeAsync(uint width, uint height)
    {
        // Not supported.
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _process?.Dispose();
    }
}
