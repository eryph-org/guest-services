using AwesomeAssertions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

/// <summary>
/// Regression tests for the SSH <c>exec</c> path. The remote command must be
/// run through the selected shell (OpenSSH's <c>$SHELL -c "&lt;command&gt;"</c>
/// contract) and handed over as a single argument — the previous
/// split-on-space exec'd the first token directly, so pipes, <c>&amp;&amp;</c>,
/// quoting and any path containing a space were silently broken.
/// </summary>
public class CommandForwarderTests
{
    [Fact]
    public void BuildStartInfo_RunsCommandThroughShell()
    {
        var startInfo = CommandForwarder.BuildStartInfo("/bin/bash", "echo hi");

        startInfo.FileName.Should().Be("/bin/bash");
        startInfo.ArgumentList.Should().Equal("-c", "echo hi");
    }

    [Fact]
    public void BuildStartInfo_KeepsPipelineAsSingleArgument()
    {
        const string command = "ls -la | grep foo && echo \"done now\"";

        var startInfo = CommandForwarder.BuildStartInfo("/bin/bash", command);

        // The whole pipeline is one argument so bash parses it — not our code.
        startInfo.ArgumentList.Should().Equal("-c", command);
    }

    [Fact]
    public void BuildStartInfo_UsesPowerShellCommandFlag()
    {
        const string command = "Get-Process | Where-Object Name -eq foo";

        var startInfo = CommandForwarder.BuildStartInfo("powershell.exe", command);

        startInfo.FileName.Should().Be("powershell.exe");
        startInfo.ArgumentList.Should().Equal("-Command", command);
    }

    [Fact]
    public void BuildStartInfo_UsesCmdCommandFlag()
    {
        const string command = "dir & echo done";

        var startInfo = CommandForwarder.BuildStartInfo("cmd.exe", command);

        startInfo.FileName.Should().Be("cmd.exe");
        startInfo.ArgumentList.Should().Equal("/c", command);
    }

    [Fact]
    public void BuildStartInfo_RedirectsStdInAndStdOutWithoutShellExecute()
    {
        var startInfo = CommandForwarder.BuildStartInfo("/bin/bash", "echo hi");

        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.RedirectStandardInput.Should().BeTrue();
        startInfo.RedirectStandardOutput.Should().BeTrue();
        startInfo.CreateNoWindow.Should().BeTrue();
    }

    [Fact]
    public void BuildStartInfo_RedirectsStdErr_SoErrorsReachTheClient()
    {
        // Regression: stderr was previously left attached to the service
        // process, so command errors never reached the SSH client.
        var startInfo = CommandForwarder.BuildStartInfo("/bin/bash", "echo hi");

        startInfo.RedirectStandardError.Should().BeTrue();
    }

    [Fact]
    public async Task PumpToProcessInputAsync_ForwardsInput_ThenClosesStdinOnEof()
    {
        // Regression for #68: the client's input must reach the child's stdin,
        // and stdin must be closed once the client half-closes the channel so a
        // stdin-reading command (cat / read / `cmd > file`) sees EOF and exits
        // instead of hanging. Before the fix the copy ran fire-and-forget and
        // stdin was never closed.
        // A non-seekable source so CopyToAsync drives it through the ReadAsync
        // loop, the way the real SshStream (a plain Stream) is pumped.
        var payload = "hello world — a payload long enough to span several reads"u8.ToArray();
        await using var source = new ReadOnlyChunkStream(payload, chunkSize: 7);
        var stdin = new CloseTrackingStream();

        await CommandForwarder.PumpToProcessInputAsync(source, stdin, CancellationToken.None);

        stdin.ToArray().Should().Equal(payload);
        stdin.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task PumpToProcessInputAsync_CompletesWithoutFaulting_WhenChildStdinIsBroken()
    {
        // The child exited while the client was still sending, so writing its
        // stdin fails with a broken-pipe IOException. The pump must treat that
        // as a normal end and still close stdin — never fault as an unobserved
        // task once the output pumps have already finished.
        await using var source = new ReadOnlyChunkStream("still sending"u8.ToArray(), chunkSize: 4);
        var stdin = new BrokenPipeStream();

        // Start up front so the assertion closure captures only the running
        // task, not the `await using` source.
        var pump = CommandForwarder.PumpToProcessInputAsync(source, stdin, CancellationToken.None);
        var act = async () => await pump;

        await act.Should().NotThrowAsync();
        stdin.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task PumpToProcessInputAsync_ClosesStdin_EvenWhenCancelled()
    {
        // The close must also happen when the run is torn down mid-copy;
        // otherwise a cancelled transfer would leave the child blocked on stdin.
        await using var source = new BlockingStream();
        var stdin = new CloseTrackingStream();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // Start the pump up front so the assertion closure captures only the
        // running task, not the disposables this test's `using`s own.
        var pump = CommandForwarder.PumpToProcessInputAsync(source, stdin, cts.Token);
        var act = async () => await pump;

        await act.Should().ThrowAsync<OperationCanceledException>();
        stdin.Disposed.Should().BeTrue();
    }

    // Records that the pump disposed the child's stdin. ToArray stays valid
    // after Dispose on MemoryStream, so the forwarded bytes are still checkable.
    private sealed class CloseTrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // A non-seekable read-only source that hands out its data in small chunks,
    // forcing CopyToAsync to loop over ReadAsync as it does for a real channel.
    private sealed class ReadOnlyChunkStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = data.Length - _position;
            if (remaining <= 0)
                return ValueTask.FromResult(0);

            var take = Math.Min(Math.Min(chunkSize, remaining), buffer.Length);
            data.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // A child-stdin stand-in whose writes fail like a broken pipe (the child is
    // gone). Still records that the pump disposed it.
    private sealed class BrokenPipeStream : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("broken pipe");

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("broken pipe");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // A read that never completes until the token is cancelled — stands in for
    // a client that keeps the channel's input side open.
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        // Stream.CopyToAsync reads through this overload, so blocking it here is
        // enough to hold the pump until the token cancels.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => 0, cancellationToken));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
