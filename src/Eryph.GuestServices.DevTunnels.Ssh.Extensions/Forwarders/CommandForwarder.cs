using System.Diagnostics;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class CommandForwarder(string command) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;

    private int _isRunning;

    public async Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            throw new InvalidOperationException("Forwarder is already running.");

        try
        {
            var splitted = command.Split(' ', 2);
            var fileName = splitted[0];
            var arguments = splitted.Length > 1 ? splitted[1] : "";
            _process = new Process();
            _process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            _ = RunAsync(stream);
        }
        catch (Exception ex)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
    }

    private async Task RunAsync(SshStream sshStream)
    {
        try
        {
            _process!.Start();

            var outputTask = _process.StandardOutput.BaseStream.CopyToAsync(sshStream, _cts.Token);
            _ = sshStream.CopyToAsync(_process.StandardInput.BaseStream, _cts.Token);
           
            await _process.WaitForExitAsync(_cts.Token);
            await outputTask;
            await sshStream.Channel.CloseAsync(unchecked((uint)_process.ExitCode), _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await sshStream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, _cts.Token);
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _process?.Kill(true);
        _process?.Dispose();
    }
}
