using System.Net.WebSockets;
using Eryph.ComputeClient;
using Eryph.ComputeClient.Models;

namespace Eryph.GuestServices.Client;

// Opens the eryph remote SSH *data plane*: starts the OpenSshChannel operation,
// polls for the one-time channel token, then connects the authenticated
// WebSocket the guest's egs SSH server is bridged to.
//
// Shared by EryphProxy (which bridges the socket to ssh's stdio for the generated
// ProxyCommand alias) and the in-process catlet file/directory commands (which
// run an SshClientSession over it via WebSocketStream), so the channel-open
// contract lives in exactly one place.
public static class EryphChannel
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    // Opens the channel and returns the connected WebSocket; the caller owns and
    // disposes it. writeWarning receives non-fatal notices (e.g. an unencrypted
    // endpoint) so each caller can route them to its own output — the proxy to
    // stderr, a command to the console. Throws GuestConnectionException with a
    // user-facing message on any failure.
    public static async Task<ClientWebSocket> OpenAsync(
        EryphConnection connection,
        string catletId,
        Action<string>? writeWarning,
        CancellationToken cancellation)
    {
        var catlets = connection.CreateCatletsClient(EryphConnection.RemoteAccessScope);
        var operations = connection.CreateOperationsClient();

        // 1. Start the control-plane operation. The agent (via the saga) prepares
        // the hvsocket and mints a one-time token.
        string operationId;
        try
        {
            operationId = (await catlets.OpenSshChannelAsync(catletId)).Value.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GuestConnectionException($"Failed to start the SSH channel: {ex.Message}");
        }

        // 2. Poll the operation until the channel token is available.
        var channelToken = await PollForTokenAsync(operations, operationId, cancellation);
        if (channelToken is null)
            throw new GuestConnectionException("The SSH channel operation did not produce a token in time.");

        // 3. Mint the bearer token and open the data-plane WebSocket with it.
        string accessToken;
        try
        {
            accessToken = await connection.GetAccessTokenAsync([EryphConnection.RemoteAccessScope]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GuestConnectionException($"Failed to acquire an access token: {ex.Message}");
        }

        var connectUri = connection.BuildComputeUri(
            $"catlets/{Uri.EscapeDataString(catletId)}/guest-services/ssh-channel/connect?token={Uri.EscapeDataString(channelToken)}");
        var wsUri = new UriBuilder(connectUri)
        {
            Scheme = connectUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        }.Uri;

        if (wsUri.Scheme == "ws")
            // The compute endpoint is not using TLS (e.g. a dev/local eryph). The
            // bearer token below is then sent in clear text; warn so a misconfigured
            // endpoint does not silently leak it.
            writeWarning?.Invoke(
                "Connecting over an unencrypted channel; the access token is not protected.");

        var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        try
        {
            await webSocket.ConnectAsync(wsUri, cancellation);
        }
        catch (Exception ex)
        {
            webSocket.Dispose();
            // Honour a real cancellation as cancellation; do not mask it as a
            // connection failure.
            if (ex is OperationCanceledException)
                throw;
            // DNS/TLS/401/upgrade failure: surface one concise message.
            throw new GuestConnectionException($"Failed to open the SSH channel connection: {ex.Message}");
        }

        return webSocket;
    }

    // Polls the operation until it completes, then returns the channel token from
    // the typed SshChannelOperationResult. Returns null on failure or timeout.
    private static async Task<string?> PollForTokenAsync(
        OperationsClient operations,
        string operationId,
        CancellationToken cancellation)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            Operation operation;
            try
            {
                operation = (await operations.GetAsync(operationId)).Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient network/auth blip must not abort the open. Keep
                // polling until the deadline; a persistent failure simply times out.
                // A cancellation, however, is honoured immediately (it is excluded
                // from this catch and propagates out of the Task.Delay below).
                await Task.Delay(PollInterval, cancellation);
                continue;
            }

            if (operation.Status == OperationStatus.Failed)
                return null;

            if (operation.Status == OperationStatus.Completed)
                return operation.Result is SshChannelOperationResult result ? result.Token : null;

            await Task.Delay(PollInterval, cancellation);
        }

        return null;
    }
}
