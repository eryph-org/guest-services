using System.Buffers;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class UploadFileForwarder(
    string basePath,
    string path,
    ulong length,
    bool overwrite)
    : IForwarder
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
            var fullPath = string.IsNullOrEmpty(basePath) ? path : Path.Combine(basePath, path);
            
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
            _ = CopyFromStreamToFileAsync(stream, (long)length);
        }
        catch (Exception ex)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
    }


    private async Task CopyFromStreamToFileAsync(SshStream sshStream, long expectedLength)
    {
        try
        {
            // Reset the file in case we are overwriting it
            _fileStream!.SetLength(0);
            const int bufferSize = (int)(2 * SshChannel.DefaultMaxPacketSize);
            using var memoryOwner = MemoryPool<byte>.Shared.Rent(bufferSize);
            var buffer = memoryOwner.Memory[..bufferSize];
            
            while (_fileStream.Length < expectedLength)
            {
                var bytesRead = await sshStream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0) break;
                await _fileStream.WriteAsync(buffer[..bytesRead], _cts.Token);
            }

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