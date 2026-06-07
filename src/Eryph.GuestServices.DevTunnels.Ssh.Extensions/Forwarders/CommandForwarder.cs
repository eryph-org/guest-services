using System.Diagnostics;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class CommandForwarder : IDisposable
{
    private readonly string _command;
    private readonly IShellSelector _selector;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;

    private int _isRunning;

    public CommandForwarder(string command, IShellSelector? selector = null)
    {
        _command = command;
        _selector = selector ?? DefaultShellSelector.Instance;
    }

    public async Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            throw new InvalidOperationException("Forwarder is already running.");

        try
        {
            // An `exec` request carries no SSH `env` (those route to ShellService
            // on the interactive channel), so resolve with no per-session
            // override: the KVP-configured shell wins, else the platform default.
            var selection = await _selector.SelectAsync(ShellOverride.Empty, cancellation);
            _process = new Process { StartInfo = BuildStartInfo(selection.Command, _command) };
            _ = RunAsync(stream);
        }
        catch (Exception ex)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
    }

    /// <summary>
    /// Builds the process start info that runs <paramref name="command"/>
    /// through <paramref name="shellCommand"/> in non-interactive command mode,
    /// honoring the OpenSSH <c>$SHELL -c "&lt;command&gt;"</c> contract. The
    /// command is handed to the shell as a single argument via
    /// <see cref="ProcessStartInfo.ArgumentList"/> so the shell parses it —
    /// rather than the previous naive split-on-space that broke pipes, quoting
    /// and any path containing a space.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string shellCommand, string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = shellCommand,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            // Capture stderr so it can be returned to the client. Without this
            // the child's errors went to the service process's own stderr and
            // were invisible to the caller — surprising for a human and broken
            // for an AI agent that needs to see why a command failed.
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(NonInteractiveShell.CommandFlagFor(shellCommand));
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private async Task RunAsync(SshStream sshStream)
    {
        try
        {
            _process!.Start();

            // This SSH library exposes only a single channel data stream — it
            // has no SSH extended-data (stderr) support — so stdout and stderr
            // are merged onto it. That mirrors `ssh host "cmd 2>&1"` and the
            // interactive PTY path, where a terminal has no separate error
            // stream. A shared lock serializes the two pumps so their writes
            // can't interleave mid-chunk and corrupt the channel framing.
            var writeLock = new SemaphoreSlim(1, 1);
            var stdoutTask = PumpToChannelAsync(
                _process.StandardOutput.BaseStream, sshStream, writeLock, _cts.Token);
            var stderrTask = PumpToChannelAsync(
                _process.StandardError.BaseStream, sshStream, writeLock, _cts.Token);
            _ = sshStream.CopyToAsync(_process.StandardInput.BaseStream, _cts.Token);

            await _process.WaitForExitAsync(_cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            await sshStream.Channel.CloseAsync(unchecked((uint)_process.ExitCode), _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await sshStream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, _cts.Token);
        }
    }

    private static async Task PumpToChannelAsync(
        Stream source,
        Stream channel,
        SemaphoreSlim writeLock,
        CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
        {
            await writeLock.WaitAsync(cancellation);
            try
            {
                await channel.WriteAsync(buffer.AsMemory(0, read), cancellation);
            }
            finally
            {
                writeLock.Release();
            }
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _process?.Kill(true);
        _process?.Dispose();
    }
}
