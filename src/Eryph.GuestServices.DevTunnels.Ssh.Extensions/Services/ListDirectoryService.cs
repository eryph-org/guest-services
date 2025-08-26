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
            await HandleListDirectoryAsync(channel, listDirectoryRequest, cancellation);
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
            
            // Get directories first
            var directories = Directory.GetDirectories(directoryPath);
            foreach (var dir in directories)
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
            
            // Get files
            var files = Directory.GetFiles(directoryPath);
            foreach (var file in files)
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

            // Serialize the file list as JSON
            var json = JsonSerializer.Serialize(fileInfos, SshExtensionUtils.FileTransferOptions);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            
            // Send the data
            var stream = new SshStream(channel);
            await stream.WriteAsync(jsonBytes, cancellation);
            await stream.FlushAsync(cancellation);
            
            // Close the stream before closing the channel
            stream.Close();
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