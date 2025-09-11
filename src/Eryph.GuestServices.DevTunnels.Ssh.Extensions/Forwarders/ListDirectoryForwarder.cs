using System.Text;
using System.Text.Json;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class ListDirectoryForwarder(string path) : IForwarder
{
    private readonly CancellationTokenSource _cts = new();
    private int _isRunning;

    public Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            throw new InvalidOperationException("Forwarder is already running.");

        // The dotnet file system APIs are synchronous. Hence, we run the directory
        // listing in a background task to avoid blocking the SSH channel.
        _ = Task.Run(async () => await ListDirectoryAsync(stream), _cts.Token);

        return Task.CompletedTask;
    }

    private async Task ListDirectoryAsync(SshStream stream)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                await stream.Channel.CloseAsync(unchecked((uint)ErrorCodes.FileNotFound), _cts.Token);
                return;
            }

            var fileInfos = new List<RemoteFileInfo>();

            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                _cts.Token.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(entry);
                    if (info.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        var dirInfo = new DirectoryInfo(entry);
                        fileInfos.Add(new RemoteFileInfo
                        {
                            Name = dirInfo.Name,
                            FullPath = dirInfo.FullName,
                            IsDirectory = true,
                            Size = 0,
                            LastModified = dirInfo.LastWriteTime
                        });
                    }
                    else
                    {
                        fileInfos.Add(new RemoteFileInfo
                        {
                            Name = info.Name,
                            FullPath = info.FullName,
                            IsDirectory = false,
                            Size = info.Length,
                            LastModified = info.LastWriteTime
                        });
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip entries we can't access, don't fail entirely
                }
            }

            // Serialize the file list as JSON
            var json = JsonSerializer.Serialize(fileInfos, SshExtensionUtils.FileTransferOptions);
            var jsonBytes = Encoding.UTF8.GetBytes(json);

            // Send the data
            await stream.WriteAsync(jsonBytes, _cts.Token);
            await stream.FlushAsync(_cts.Token);
            await stream.Channel.CloseAsync(_cts.Token);
        }
        catch (UnauthorizedAccessException)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, "Access denied", _cts.Token);
        }
        catch (Exception ex)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, _cts.Token);
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}
