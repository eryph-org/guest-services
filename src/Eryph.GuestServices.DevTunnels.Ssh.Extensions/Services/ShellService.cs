using System.Collections.Concurrent;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Eryph.GuestServices.Pty.Windows;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;
using System.Threading.Channels;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = ChannelRequestTypes.Shell)]
[ServiceActivation(ChannelRequest = ChannelRequestTypes.Terminal)]
[ServiceActivation(ChannelRequest = "window-change")]
public class ShellService(SshSession session) : SshService(session)
{
    private readonly ConcurrentDictionary<uint, PtyInstance> _instances = new();
    private readonly SemaphoreSlim _lock = new(1, 1);


    // TODO add signal support

    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        switch (request.RequestType)
        {
            case ChannelRequestTypes.Shell:
                var message = request.Request.ConvertTo<ShellRequestMessage>();
                return OnShellRequestAsync(channel, request, message, cancellation);

            case ChannelRequestTypes.Terminal:
                var terminalMessage = request.Request.ConvertTo<TerminalRequestMessage>();
                return OnTerminalRequestAsync(channel, request, terminalMessage, cancellation);

            case "window-change":
                var windowChangeMessage = request.Request.ConvertTo<WindowChangeRequestMessage>();
                return OnWindowChangeRequestAsync(channel, request, windowChangeMessage, cancellation);
        }

        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
        return Task.CompletedTask;
    }

    private Task OnShellRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        ShellRequestMessage requestMessage,
        CancellationToken cancellation)
    {
        
        var pty = GetPtyInstance(channel.ChannelId);
        request.ResponseContinuation = async (response) =>
        {
            var stream = new SshStream(channel);
            var result = await pty.RunAsync(stream, request.Cancellation);
            await channel.CloseAsync(unchecked((uint)result), cancellation);
        };

        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        return Task.CompletedTask;
    }

    private async Task OnTerminalRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        TerminalRequestMessage requestMessage,
        CancellationToken cancellation)
    {
        var pty = GetPtyInstance(channel.ChannelId);
        await pty.ResizeAsync(requestMessage.Columns, requestMessage.Rows, request.Cancellation);
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
    }

    private async Task OnWindowChangeRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        WindowChangeRequestMessage requestMessage,
        CancellationToken cancellation)
    {
        var pty = GetPtyInstance(channel.ChannelId);
        await pty.ResizeAsync(requestMessage.Width, requestMessage.Height, request.Cancellation);
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var instance in _instances.Values.ToArray())
        {
            instance.Dispose();
        }

        base.Dispose(disposing);
    }

    private PtyInstance GetPtyInstance(uint channelId)
    {
        return _instances.GetOrAdd(channelId, (_) => new PtyInstance());
    }
}
