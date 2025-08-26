using System.Collections.Concurrent;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

public abstract class FileTransferServiceBase<TRequest>(SshSession session) : SshService(session)
    where TRequest : ChannelRequestMessage, IFileTransferRequestMessage, new()
{
    private readonly ConcurrentDictionary<uint, FileForwarder> _forwarders = new();
    protected abstract string RequestType { get; }
    protected abstract FileTransferDirection Direction { get; }

    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        if (request.RequestType != RequestType)
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        var fileTransferRequest = request.Request.ConvertTo<TRequest>();
        var forwarder = new FileForwarder(
            fileTransferRequest.Path,
            fileTransferRequest.FileName,
            fileTransferRequest.Length,
            fileTransferRequest.Overwrite,
            Direction);
        
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