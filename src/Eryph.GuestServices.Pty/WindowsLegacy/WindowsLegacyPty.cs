using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Eryph.GuestServices.Pty.WindowsLegacy;

public sealed class WindowsLegacyPty : IPty
{
    private int _disposed;

    private Process? _process;

    public Stream? Input { get; private set; }

    public Stream? Output { get; private set; }

    [MemberNotNull(nameof(_process), nameof(Input), nameof(Output))]
    public Task StartAsync(uint width, uint height, string command)
    {
        var shellHostPath = Path.Combine(
            Assembly.GetExecutingAssembly().Location,
            "native", "win-x64", "ssh-shellhost.exe");
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shellHostPath,
                Arguments = $"---pty {command}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        _process.Start();
        Input = _process.StandardInput.BaseStream;
        Output = _process.StandardOutput.BaseStream;
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        await _process!.WaitForExitAsync(cancellation);
        return _process.ExitCode;
    }

    public Task ResizeAsync(uint width, uint height)
    {
        // The resize handling in ssh-shellhost.exe is currently disabled:
        // https://github.com/PowerShell/openssh-portable/blob/v9.8.3.0/contrib/win32/win32compat/shell-host.c#L803-L854
        // Hence, we have not implemented any handling for it.
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _process?.Kill(true);
        _process?.Dispose();
    }
}
