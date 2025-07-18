using System.Buffers;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = CustomChannelRequestTypes.UploadFile)]
public class UploadFileService(SshSession session) : SshService(session)
{
    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        var fileTransferRequest = request.Request.ConvertTo<UploadFileRequestMessage>();

        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async _ =>
        {
            try
            {
                var expectedLength = (long)fileTransferRequest.Length;
                var stream = new SshStream(channel);
                using var memoryOwner = MemoryPool<byte>.Shared.Rent((int)(2 * SshChannel.DefaultMaxPacketSize));
                var buffer = memoryOwner.Memory;

                if (!fileTransferRequest.Overwrite && File.Exists(fileTransferRequest.Path))
                {
                    await channel.CloseAsync(unchecked((uint)ErrorCodes.FileExists), request.Cancellation);
                    return;
                }

                var directory = Path.GetDirectoryName(fileTransferRequest.Path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var fileStream =
                    new FileStream(fileTransferRequest.Path, FileMode.OpenOrCreate, FileAccess.Write);
                while (fileStream.Length < expectedLength)
                {
                    var bytesRead = await stream.ReadAsync(buffer, request.Cancellation);
                    if (bytesRead == 0)
                    {
                        break; // End of stream
                    }

                    await fileStream.WriteAsync(buffer[..bytesRead], request.Cancellation);
                }

                await fileStream.FlushAsync(request.Cancellation);
            }
            catch (Exception ex)
            {
                await channel.CloseAsync("exception@eryph.io", ex.Message, request.Cancellation);
                return;
            }

            await channel.CloseAsync(0, request.Cancellation);
        };

        return Task.CompletedTask;
    }
}
