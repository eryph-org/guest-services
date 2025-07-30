using System.Collections.Concurrent;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Eryph.GuestServices.Pty;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = ChannelRequestTypes.Shell)]
[ServiceActivation(ChannelRequest = ChannelRequestTypes.Terminal)]
[ServiceActivation(ChannelRequest = "window-change")]
public class ShellService(SshSession session) : SshService(session)
{
    private readonly ConcurrentDictionary<uint, PtyForwarder> _instances = new();

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
        if (!_instances.TryGetValue(channel.ChannelId, out var pty))
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        if (pty.IsRunning)
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async (_) =>
        {
            var stream = new SshStream(channel);
            try
            {
                await pty.StartAsync(stream, request.Cancellation);
            }
            catch (Exception ex)
            {
                var result = ex is PtyException pe ? pe.Result : PtyErrorCodes.GenericError;
                await channel.CloseAsync(unchecked((uint)result), cancellation);
            }
        };

        return Task.CompletedTask;
    }

    private async Task OnTerminalRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        TerminalRequestMessage requestMessage,
        CancellationToken cancellation)
    {
        var pty = new PtyForwarder();
        if (!_instances.TryAdd(channel.ChannelId, pty))
        {
            pty.Dispose();
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return;
        }

        channel.Closed += (_, _ ) => 
        {
            _instances.TryRemove(channel.ChannelId, out _);
            pty.Dispose();
        };
        
        await pty.ResizeAsync(requestMessage.Columns, requestMessage.Rows, request.Cancellation);
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
    }

    private Task OnWindowChangeRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        WindowChangeRequestMessage requestMessage,
        CancellationToken cancellation)
    {
        if (!_instances.TryGetValue(channel.ChannelId, out var pty))
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async (_) =>
        {
            await pty.ResizeAsync(requestMessage.Width, requestMessage.Height, request.Cancellation);
        };

        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var instance in _instances.Values.ToArray())
        {
            instance.Dispose();
        }

        base.Dispose(disposing);
    }
}
