using System.Collections.Concurrent;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = "subsystem")]
public class SubsystemService(SshSession session) : SshService(session)
{
    private readonly ConcurrentDictionary<uint, PowershellForwarder> _forwarders = new();

    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        if (request.RequestType != "subsystem")
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        var subsystemRequest = request.Request.ConvertTo<SubsystemRequestMessage>();
        if (subsystemRequest.Name != "powershell")
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }
        
        var forwarder = new PowershellForwarder();
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
}
