using System.Collections.Concurrent;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = EryphChannelRequestTypes.ListDirectory)]
public class ListDirectoryService(SshSession session) : SshService(session)
{
    private readonly ConcurrentDictionary<uint, ListDirectoryForwarder> _forwarders = new();

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
        var forwarder = new ListDirectoryForwarder(listDirectoryRequest.Path);
        
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
            // Use Task.Run to prevent blocking the SSH channel
            await Task.Run(() => forwarder.StartAsync(stream, request.Cancellation), request.Cancellation);
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