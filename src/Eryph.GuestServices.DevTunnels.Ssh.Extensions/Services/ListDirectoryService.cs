using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;
using System.Text;
using System.Text.Json;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = EryphChannelRequestTypes.ListDirectory)]
public class ListDirectoryService(SshSession session) : SshService(session)
{
    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        if (request.RequestType != EryphChannelRequestTypes.ListDirectory)
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        var listDirectoryRequest = request.Request.ConvertTo<ListDirectoryRequestMessage>();
        
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async _ =>
        {
            await HandleListDirectoryAsync(channel, listDirectoryRequest, request.Cancellation);
        };

        return Task.CompletedTask;
    }

    private async Task HandleListDirectoryAsync(SshChannel channel, ListDirectoryRequestMessage request, CancellationToken cancellation)
    {
        try
        {
            var directoryPath = request.Path;
            
            if (!Directory.Exists(directoryPath))
            {
                await channel.CloseAsync(unchecked((uint)ErrorCodes.FileNotFound), cancellation);
                return;
            }

            var fileInfos = new List<RemoteFileInfo>();
            
            // Get directories first - with better error handling
            try
            {
                var directories = Directory.GetDirectories(directoryPath);
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
                var files = Directory.GetFiles(directoryPath);
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
            var stream = new SshStream(channel);
            await stream.WriteAsync(jsonBytes, cancellation);
            await stream.FlushAsync(cancellation);
            await channel.CloseAsync(cancellation);
        }
        catch (UnauthorizedAccessException)
        {
            await channel.CloseAsync(EryphSignalTypes.Exception, "Access denied", cancellation);
        }
        catch (Exception ex)
        {
            await channel.CloseAsync(EryphSignalTypes.Exception, ex.Message, cancellation);
        }
    }
}