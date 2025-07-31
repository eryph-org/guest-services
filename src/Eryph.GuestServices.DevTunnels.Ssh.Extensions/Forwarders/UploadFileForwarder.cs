using System.Buffers;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class UploadFileForwarder(string path, string fileName, ulong length, bool overwrite) : IDisposable
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
            var fullPath = string.IsNullOrEmpty(fileName) ? path : Path.Combine(path, fileName);
            
            if (!overwrite && File.Exists(fullPath))
            {
                await stream.Channel.CloseAsync(unchecked((uint)ErrorCodes.FileExists), cancellation);
                return;
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _fileStream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write);
            _ = CopyAsync(stream, (long) length);
        }
        catch (Exception ex)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
    }

    private async Task CopyAsync(SshStream sshStream, long expectedLength)
    {
        try
        {
            // Reset the file in case we are overwriting it
            _fileStream!.SetLength(0);
            using var memoryOwner = MemoryPool<byte>.Shared.Rent((int)(2 * SshChannel.DefaultMaxPacketSize));
            var buffer = memoryOwner.Memory;
            while (_fileStream.Length < expectedLength)
            {
                var bytesRead = await sshStream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0)
                    break;
                
                await _fileStream.WriteAsync(buffer[..bytesRead], _cts.Token);
            }

            // Explicitly flush and dispose the file stream to ensure all data is written.
            // By closing the channel, we confirm to the client the file has been written successfully.
            await _fileStream.FlushAsync(_cts.Token);
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
