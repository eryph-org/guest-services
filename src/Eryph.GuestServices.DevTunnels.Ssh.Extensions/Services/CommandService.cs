using System.Collections.Concurrent;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = ChannelRequestTypes.Command)]
public class CommandService : SshService
{
    private readonly IShellSelector? _shellSelector;
    private readonly ConcurrentDictionary<uint, CommandForwarder> _forwarders = new();

    // Microsoft.DevTunnels.Ssh activation picks the 2-arg ctor when a config
    // object (the shell selector) is present in SshSessionConfiguration.Services.
    public CommandService(SshSession session) : this(session, null) { }

    public CommandService(SshSession session, IShellSelector? shellSelector) : base(session)
    {
        _shellSelector = shellSelector;
    }

    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        if (request.RequestType != ChannelRequestTypes.Command)
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        var execRequest = request.Request.ConvertTo<CommandRequestMessage>();
        var forwarder = new CommandForwarder(execRequest.Command ?? "", _shellSelector);

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
