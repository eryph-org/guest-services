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
            // Only selection.Command is used: selection.Arguments carries the
            // shell's *interactive* flags (e.g. `-i`, `-WindowStyle Hidden`),
            // which are wrong for a non-interactive exec. The command-mode flag
            // (`-c` / `-Command` / `/c`) is supplied by BuildStartInfo instead.
            _process = new Process { StartInfo = BuildStartInfo(selection.Command, _command) };
            ObserveFault(RunAsync(stream));
        }
        catch (Exception ex)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
    }

    // Keeps the fire-and-forget RunAsync from surfacing its fault as an
    // unobserved task exception — e.g. the OperationCanceledException that
    // WaitForExitAsync throws when Dispose cancels the token mid-run.
    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

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
        // This SSH library exposes only a single channel data stream — it has
        // no SSH extended-data (stderr) support — so stdout and stderr are
        // merged onto it. That mirrors `ssh host "cmd 2>&1"` and the interactive
        // PTY path, where a terminal has no separate error stream. A shared lock
        // serializes the two pumps so their writes can't interleave mid-chunk
        // and corrupt the channel framing.
        // Capture the token once: a CancellationToken struct stays usable after
        // its source is disposed, so Dispose can free _cts without racing the
        // _cts.Token getter here (which would throw ObjectDisposedException).
        var cancellation = _cts.Token;
        var writeLock = new SemaphoreSlim(1, 1);
        Task? stdoutTask = null;
        Task? stderrTask = null;
        Task? stdinTask = null;
        try
        {
            _process!.Start();

            stdoutTask = PumpToChannelAsync(
                _process.StandardOutput.BaseStream, sshStream, writeLock, cancellation);
            stderrTask = PumpToChannelAsync(
                _process.StandardError.BaseStream, sshStream, writeLock, cancellation);
            // Forward the client's input to the child and close its stdin once
            // the client half-closes the channel, so stdin-reading commands see
            // EOF and finish instead of hanging forever (issue #68).
            stdinTask = PumpToProcessInputAsync(
                sshStream, _process.StandardInput.BaseStream, cancellation);

            await _process.WaitForExitAsync(cancellation);
            await Task.WhenAll(stdoutTask, stderrTask);
            await sshStream.Channel.CloseAsync(unchecked((uint)_process.ExitCode), cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await sshStream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
        finally
        {
            // Dispose the per-run lock, but only once every pump has stopped
            // touching it. On the success path the output pumps completed above;
            // the stdin pump can still be blocked on the channel read (a command
            // may exit without draining its input), so cancel to unwind it too,
            // then wait out the teardown race before disposing.
            var pending =
                stdoutTask is { IsCompleted: false }
                || stderrTask is { IsCompleted: false }
                || stdinTask is { IsCompleted: false };
            if (pending)
            {
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
                var running = new List<Task>(3);
                if (stdoutTask is not null) running.Add(stdoutTask);
                if (stderrTask is not null) running.Add(stderrTask);
                if (stdinTask is not null) running.Add(stdinTask);
                try { await Task.WhenAll(running); } catch { /* teardown race */ }
            }
            writeLock.Dispose();
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

    // Copies the client's input to the child's stdin, then closes stdin so the
    // child sees EOF. Without the close, a non-PTY command that reads stdin
    // (`cat`, shell `read`, a `cmd > file` redirect fed from a pipe) blocks
    // forever once the client half-closes the channel, because the write end of
    // the stdin pipe stays open. Closing on every exit — EOF, cancel, or a
    // broken-pipe fault after the child already exited — is what unblocks it.
    internal static async Task PumpToProcessInputAsync(
        Stream source, Stream processInput, CancellationToken cancellation)
    {
        try
        {
            await source.CopyToAsync(processInput, cancellation);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child already closed its stdin — it exited (or was killed)
            // while the client was still sending — so the write hit a broken
            // pipe. Nothing is left to forward; treat it as a normal end rather
            // than faulting this task, which (once stdout/stderr have already
            // completed) would surface as an unobserved task exception.
        }
        finally
        {
            try { await processInput.DisposeAsync(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        // Runs on the channel.Closed event path, so it must not throw. Cancel
        // first so the pumps observe cancellation and stop, instead of lingering
        // until the killed process closes the pipes (matches PtyForwarder).
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        // Kill throws InvalidOperationException if the process was never started
        // (Dispose can race a teardown before RunAsync calls Start). Best-effort
        // — ObjectDisposedException derives from InvalidOperationException, so
        // this one catch covers both.
        try { _process?.Kill(true); }
        catch (InvalidOperationException) { }

        // Must not throw: the input pump may have already disposed stdin's
        // underlying stream, and disposing the process flushes that stream on
        // some platforms.
        try { _process?.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        _cts.Dispose();
    }
}
