using System.Buffers;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = CustomChannelRequestTypes.FileTransfer)]
public class FileTransferService(SshSession session) : SshService(session)
{
    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        var fileTransferRequest = request.Request.ConvertTo<FileTransferRequestMessage>();

        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async _ =>
        {
            var expectedLength = (long)fileTransferRequest.Length;
            var stream = new SshStream(channel);
            using var memoryOwner = MemoryPool<byte>.Shared.Rent((int)(2 * SshChannel.DefaultMaxPacketSize));
            var buffer = memoryOwner.Memory;
            await using var fileStream = new FileStream(fileTransferRequest.Path, FileMode.OpenOrCreate, FileAccess.Write);
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
            await channel.CloseAsync(0, request.Cancellation);
        };

        return Task.CompletedTask;
    }
}
