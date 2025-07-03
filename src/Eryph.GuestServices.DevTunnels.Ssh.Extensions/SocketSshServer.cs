// Based on https://github.com/microsoft/dev-tunnels-ssh/blob/main/src/cs/Ssh.Tcp/SshServer.cs
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using static System.Collections.Specialized.BitVector32;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public sealed class SocketSshServer : IDisposable
{
    private readonly SshSessionConfiguration _config;
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();
    private readonly object _lock = new();
    private readonly IList<SshServerSession> _sessions = [];
    private readonly TraceSource _trace;

    public SocketSshServer(SshSessionConfiguration config, TraceSource trace)
    {
        _config = config;
        _trace = trace;
    }

    public SshServerCredentials Credentials { get; set; } = new();

    public event EventHandler<SshAuthenticatingEventArgs>? SessionAuthenticating;

    public event EventHandler<SshChannelOpeningEventArgs>? ChannelOpening;

    public event EventHandler<SshRequestEventArgs<ChannelRequestMessage>>? ChannelRequest;

    public event EventHandler<Exception>? ExceptionRaised;

    public async Task AcceptSessionsAsync(Socket serverSocket)
    {
        while (!_disposeCancellationTokenSource.IsCancellationRequested)
        {
            var socket = await serverSocket.AcceptAsync().ConfigureAwait(false);
            ConfigureSocketOptionsForSsh(socket);
            var session = new SshServerSession(_config, _trace);
            session.Credentials = Credentials;

            lock (_lock)
            {
                _sessions.Add(session);
            }

            session.Authenticating += (s, e) =>
            {
                SessionAuthenticating?.Invoke(s, e);
            };

            session.ChannelOpening += (s, e) =>
            {
                ChannelOpening?.Invoke(s, e);
                if (e.FailureReason == SshChannelOpenFailureReason.None)
                {
                    e.Channel.Request += (cs, ce) =>
                    {
                        ChannelRequest?.Invoke(cs, ce);
                    };
                }
            };

            session.Closed += (_, _) =>
            {
                lock (_lock)
                {
                    _sessions.Remove(session);
                }
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var stream = new NetworkStream(socket, true);
                    await session.ConnectAsync(stream, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SshConnectionException ex)
                {
                    await session.CloseAsync(ex.DisconnectReason, ex).ConfigureAwait(false);
                    ExceptionRaised?.Invoke(this, ex);
                }
                catch (Exception ex)
                {
                    await session.CloseAsync(SshDisconnectReason.ProtocolError, ex)
                        .ConfigureAwait(false);
                    ExceptionRaised?.Invoke(this, ex);
                }
            });

        }
    }

    private static void ConfigureSocketOptionsForSsh(Socket socket)
    {
        const int bufferSize = (int)(2 * SshChannel.DefaultMaxPacketSize);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, bufferSize);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, bufferSize);
    }

    public void Dispose()
    {
        try
        {
            _disposeCancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException) { }

        _disposeCancellationTokenSource.Dispose();

        foreach (var session in _sessions.ToArray())
        {
            session.Dispose();
        }

        lock (_lock)
        {
            _sessions.Clear();
        }
    }
}
