using System.Text;
using System.Text.Json;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public sealed class ListDirectoryForwarder(string path) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _isRunning;

    public async Task StartAsync(SshStream stream, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            throw new InvalidOperationException("Forwarder is already running.");

        try
        {
            if (!Directory.Exists(path))
            {
                await stream.Channel.CloseAsync(unchecked((uint)ErrorCodes.FileNotFound), cancellation);
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
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, "Access denied", cancellation);
        }
        catch (Exception ex)
        {
            await stream.Channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}