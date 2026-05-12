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
[ServiceActivation(ChannelRequest = "env")]
public class ShellService : SshService
{
    private readonly IShellSelector? _shellSelector;
    private readonly ConcurrentDictionary<uint, Lazy<PtyForwarder>> _instances = new();

    // Microsoft.DevTunnels.Ssh activation will pick the 2-arg ctor when a
    // config object is present in SshSessionConfiguration.Services.
    public ShellService(SshSession session) : this(session, null) { }

    public ShellService(SshSession session, IShellSelector? shellSelector) : base(session)
    {
        _shellSelector = shellSelector;
    }

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

            case "env":
                var envMessage = request.Request.ConvertTo<EnvironmentVariableRequestMessage>();
                return OnEnvironmentRequestAsync(channel, request, envMessage, cancellation);
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
        // RFC 4254 §6.5 allows a `shell` request without a prior `pty-req` or
        // `env`. Lazily create the forwarder so a bare shell request still
        // works; the PTY backend allocates a default-sized pseudo-console.
        var pty = GetOrAddForwarder(channel);

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
        var pty = GetOrAddForwarder(channel);

        await pty.ResizeAsync(requestMessage.Columns, requestMessage.Rows, request.Cancellation);
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
    }

    private Task OnWindowChangeRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        WindowChangeRequestMessage requestMessage,
        CancellationToken cancellation)
    {
        if (!_instances.TryGetValue(channel.ChannelId, out var lazyPty))
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        var pty = lazyPty.Value;
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async (_) =>
        {
            await pty.ResizeAsync(requestMessage.Width, requestMessage.Height, request.Cancellation);
        };

        return Task.CompletedTask;
    }

    private Task OnEnvironmentRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        EnvironmentVariableRequestMessage requestMessage,
        CancellationToken cancellation)
    {
        // The env request can arrive before pty-req, so create the forwarder
        // lazily on first env. The forwarder reports back whether it actually
        // stored the value (recognized name + forwarder not yet started); we
        // mirror that into the SSH channel response so clients don't think an
        // unhonored env var was applied.
        var pty = GetOrAddForwarder(channel);
        var accepted = pty.TrySetEnvironmentVariable(requestMessage.Name, requestMessage.Value);

        request.ResponseTask = Task.FromResult<SshMessage>(
            accepted ? new ChannelSuccessMessage() : new ChannelFailureMessage());
        return Task.CompletedTask;
    }

    private PtyForwarder GetOrAddForwarder(SshChannel channel)
    {
        // Lazy<T> with default (ExecutionAndPublication) mode guarantees the
        // factory runs at most once even if GetOrAdd's outer factory races —
        // otherwise we could end up with multiple PtyForwarders and duplicate
        // channel.Closed subscriptions.
        var lazy = _instances.GetOrAdd(channel.ChannelId, id => new Lazy<PtyForwarder>(() =>
        {
            var forwarder = new PtyForwarder(_shellSelector);
            channel.Closed += (_, _) =>
            {
                _instances.TryRemove(id, out _);
                forwarder.Dispose();
            };
            return forwarder;
        }));
        return lazy.Value;
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var lazy in _instances.Values.ToArray())
        {
            if (lazy.IsValueCreated)
                lazy.Value.Dispose();
        }

        base.Dispose(disposing);
    }
}
