using System.Buffers;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class DownloadFileForwarder(string path) : IForwarder
{
    private readonly CancellationTokenSource _cts = new();
    private FileStream? _fileStream;
    private int _isRunning;

    public async Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            throw new InvalidOperationException("Forwarder is already running.");

        try
        {
            if (!File.Exists(path))
            {
                await stream.Channel.CloseAsync(unchecked((uint)ErrorCodes.FileNotFound), cancellation);
                return;
            }

            _fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            _ = CopyFromFileToStreamAsync(stream);
        }
        catch (Exception ex)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
    }

    private async Task CopyFromFileToStreamAsync(SshStream sshStream)
    {
        try
        {
            const int bufferSize = (int)(2 * SshChannel.DefaultMaxPacketSize);
            using var memoryOwner = MemoryPool<byte>.Shared.Rent(bufferSize);
            var buffer = memoryOwner.Memory[..bufferSize];
            
            int bytesRead;
            while ((bytesRead = await _fileStream!.ReadAsync(buffer, _cts.Token)) > 0)
            {
                await sshStream.WriteAsync(buffer[..bytesRead], _cts.Token);
            }

            await _fileStream.DisposeAsync();
            await sshStream.Channel.CloseAsync(_cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await sshStream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, _cts.Token);
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _fileStream?.Dispose();
    }
}