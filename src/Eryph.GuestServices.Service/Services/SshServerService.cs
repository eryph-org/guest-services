using System.Diagnostics;
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

// TODO migrate to IHostedService
internal sealed class SshServerService(
    IKeyStorage keyStorage,
    IHostKeyGenerator hostKeyGenerator,
    IGuestDataExchange guestDataExchange,
    IClientKeyProvider clientKeyProvider,
    ILogger<SshServerService> logger) : BackgroundService
{
    private SocketSshServer? _server;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
        
        _server = new SocketSshServer(config, new TraceSource("SshServer"));
        _server.Credentials = new SshServerCredentials(hostKey);
        _server.SessionAuthenticating += SessionAuthenticating;
        _server.ExceptionRaised += ExceptionRaised;

        await using var _ = stoppingToken.Register(_server.Dispose);
        using var socket = await SocketFactory.CreateServerSocket(ListenMode.Parent, Constants.ServiceId, 1);
        await Task.WhenAll(
            _server.AcceptSessionsAsync(socket),
            SetStatusAsync());
    }

    private async Task SetStatusAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        var values = new Dictionary<string, string?>
        {
            [Constants.StatusKey] = "available"
        };
        await guestDataExchange.SetGuestValuesAsync(values);
    }

    private void ExceptionRaised(object? sender, Exception e)
    {
        logger.LogWarning(e, "Exception is SSH server");
    }

    private void SessionAuthenticating(object? _, SshAuthenticatingEventArgs e)
    {
        
        logger.LogWarning("Authentication type: {AuthType}", e.AuthenticationType);
        if (e.AuthenticationType is not (SshAuthenticationType.ClientPublicKey
            or SshAuthenticationType.ClientPublicKeyQuery))
            return;

        if (e.Username != "egs")
        {
            logger.LogWarning("Incorrect user name {Username}", e.Password);
            return;
        }

        if (e.PublicKey is null)
        {
            logger.LogWarning("Public key is null for user {Username}", e.Username);
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

    public override void Dispose()
    {
        var values = new Dictionary<string, string?>
        {
            [Constants.StatusKey] = null,
        };
        guestDataExchange.SetGuestValuesAsync(values).GetAwaiter().GetResult();
        base.Dispose();
        _server?.Dispose();
    }
}
