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
            
            // Get directories first - with better error handling
            try
            {
                var directories = Directory.GetDirectories(path);
                foreach (var dir in directories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        fileInfos.Add(new RemoteFileInfo
                        {
                            Name = dirInfo.Name,
                            FullPath = dirInfo.FullName,
                            IsDirectory = true,
                            Size = 0,
                            LastModified = dirInfo.LastWriteTime
                        });
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip directories we can't access, don't fail entirely
                        continue;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // If we can't list directories at all, continue with files only
            }
            
            // Get files - with better error handling
            try
            {
                var files = Directory.GetFiles(path);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        fileInfos.Add(new RemoteFileInfo
                        {
                            Name = fileInfo.Name,
                            FullPath = fileInfo.FullName,
                            IsDirectory = false,
                            Size = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime
                        });
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip files we can't access, don't fail entirely
                        continue;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // If we can't list files at all, but we could access the directory, 
                // return empty list rather than failing
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