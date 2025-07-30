using System.Collections.Concurrent;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = CustomChannelRequestTypes.UploadFile)]
public class UploadFileService(SshSession session) : SshService(session)
{
    private readonly ConcurrentDictionary<uint, UploadFileForwarder> _forwarders = new();

    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        if (request.RequestType != CustomChannelRequestTypes.UploadFile)
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        var fileTransferRequest = request.Request.ConvertTo<UploadFileRequestMessage>();
        var forwarder = new UploadFileForwarder(
            fileTransferRequest.Path,
            fileTransferRequest.FileName,
            fileTransferRequest.Length,
            fileTransferRequest.Overwrite);
        
        if (!_forwarders.TryAdd(channel.ChannelId, forwarder))
        {
            forwarder.Dispose();
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        channel.Closed += (_, _) =>
        {
            _forwarders.TryRemove(channel.ChannelId, out _);
            forwarder.Dispose();
        };
        
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async _ =>
        {
            var stream = new SshStream(channel);
            await forwarder.StartAsync(stream, request.Cancellation);
        };

        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var forwarder in _forwarders.Values.ToArray())
        {
            forwarder.Dispose();
        }

        base.Dispose(disposing);
    }
}
