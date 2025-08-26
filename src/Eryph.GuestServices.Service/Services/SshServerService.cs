using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

internal sealed class SshServerService(
    IKeyStorage keyStorage,
    IHostKeyGenerator hostKeyGenerator,
    IGuestDataExchange guestDataExchange,
    IClientKeyProvider clientKeyProvider,
    ILogger<SshServerService> logger) : IHostedService, IAsyncDisposable
{
    private Socket? _socket;
    private SocketSshServer? _server;
    private Task? _listenTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();

        var hostKey = await keyStorage.GetHostKeyAsync();
        if (hostKey is null)
        {
            logger.LogInformation("No host key found. Generating a new one.");
            hostKey = hostKeyGenerator.GenerateHostKey();
            await keyStorage.SetHostKeyAsync(hostKey);
        }

        var config = new SshSessionConfiguration(useSecurity: true);

        config.Services.Add(typeof(SubsystemService), null);
        config.Services.Add(typeof(CommandService), null);
        config.Services.Add(typeof(ShellService), null);
        config.Services.Add(typeof(UploadFileService), null);
        config.Services.Add(typeof(DownloadFileService), null);

        _server = new SocketSshServer(config, new TraceSource("SshServer"));
        _server.Credentials = new SshServerCredentials(hostKey);
        _server.SessionAuthenticating += SessionAuthenticating;
        _server.ExceptionRaised += ExceptionRaised;

        _socket = await SocketFactory.CreateServerSocket(ListenMode.Parent, Constants.ServiceId, 1);
        _listenTask = _server.AcceptSessionsAsync(_socket);

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        await SetStatusAsync("available");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await SetStatusAsync(null);
        _server?.Dispose();
        if (_listenTask is not null)
            await _listenTask.WaitAsync(cancellationToken);
        
        _socket?.Dispose();
    }

    private void ExceptionRaised(object? sender, Exception e)
    {
        logger.LogWarning(e, "Exception is SSH server");
    }

    private void SessionAuthenticating(object? _, SshAuthenticatingEventArgs e)
    {
        logger.LogInformation("Authenticating session: {AuthType}", e.AuthenticationType);
        if (e.AuthenticationType is not (SshAuthenticationType.ClientPublicKey or SshAuthenticationType.ClientPublicKeyQuery))
            return;

        if (e.Username != "egs")
        {
            logger.LogInformation("Incorrect user name {Username}", e.Username);
            return;
        }

        if (e.PublicKey is null)
        {
            logger.LogInformation("Public key is null for user {Username}", e.Username);
            return;
        }

        e.AuthenticationTask = CheckClientKey(e.PublicKey);
    }

    private async Task<ClaimsPrincipal?> CheckClientKey(IKeyPair clientKey)
    {
        var expectedClientKey = await clientKeyProvider.GetClientKey();
        if (expectedClientKey is null)
        {
            logger.LogInformation("Failed to authenticate client. The client public key is missing.");
            return null;
        }

        if (clientKey.GetPublicKeyBytes() != expectedClientKey.GetPublicKeyBytes())
        {
            logger.LogInformation(
                "Failed to authenticate client. The provided public does not match the expected public key: {FingerPrint}.",
                expectedClientKey.GetFingerPrint());
            return null;
        }
        return new ClaimsPrincipal();
    }

    public async ValueTask DisposeAsync()
    {
        await SetStatusAsync(null);
        _server?.Dispose();
        _socket?.Dispose();
    }

    private async Task SetStatusAsync(string? status)
    {
        var values = new Dictionary<string, string?>
        {
            [Constants.StatusKey] = status,
        };
        await guestDataExchange.SetGuestValuesAsync(values);
    }
}
