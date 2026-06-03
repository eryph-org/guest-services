using System.Net.WebSockets;
using Eryph.ComputeClient;
using Eryph.ComputeClient.Models;

namespace Eryph.GuestServices.Tool.Eryph;

// The eryph data plane. Invoked by SSH as the ProxyCommand
// (egs-tool eryph proxy <catletId>). Two-step, using the typed Eryph.ComputeClient
// (eryph's async operation model — the API never blocks on an operation):
//   1. CatletsClient.OpenSshChannel  -> starts the OpenSshChannel operation.
//   2. poll OperationsClient.Get     -> until it completes; read the one-time
//                                       channel token from the SshChannelOperationResult.
//   3. GET catlets/{id}/ssh-channel/connect?token=...  (WebSocket) -> bridge the
//      local stdin/stdout to that socket. The WebSocket leg is a raw ClientWebSocket
//      because OpenAPI/the generated client does not model WebSocket upgrades.
// Protocol-agnostic: SSH runs end-to-end over the channel, eryph only relays the
// bytes. Run outside Spectre.Console.Cli so nothing touches the redirected
// stdin/stdout.
public static class EryphProxy
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(string catletId)
    {
        var connection = EryphConnection.Resolve();
        if (connection is null)
        {
            await Console.Error.WriteLineAsync(
                "Could not find an eryph connection. Is eryph configured or eryph-zero running?");
            return -1;
        }

        var catlets = connection.CreateCatletsClient();
        var operations = connection.CreateOperationsClient();

        // 1. Start the control-plane operation. The agent (via the saga) prepares the hvsocket and
        // mints a one-time token; we do not push a key here (use `eryph add-key` for the added-key flow).
        string operationId;
        try
        {
            operationId = (await catlets.OpenSshChannelAsync(catletId)).Value.Id;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to start the SSH channel: {ex.Message}");
            return -1;
        }

        // 2. Poll the operation until the channel token is available.
        var channelToken = await PollForTokenAsync(operations, operationId);
        if (channelToken is null)
        {
            await Console.Error.WriteLineAsync("The SSH channel operation did not produce a token in time.");
            return -1;
        }

        // 3. Open the data-plane WebSocket with the token and bridge stdio.
        var token = await connection.GetAccessTokenAsync();
        var connectUri = connection.BuildComputeUri(
            $"catlets/{catletId}/ssh-channel/connect?token={Uri.EscapeDataString(channelToken)}");
        var wsUri = new UriBuilder(connectUri)
        {
            Scheme = connectUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        }.Uri;

        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        await webSocket.ConnectAsync(wsUri, CancellationToken.None);

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        using var cts = new CancellationTokenSource();
        await Task.WhenAll(
            PumpStdinToSocketAsync(stdin, webSocket, cts),
            PumpSocketToStdoutAsync(webSocket, stdout, cts));

        return 0;
    }

    // Polls the operation until it completes, then returns the channel token from the typed
    // SshChannelOperationResult. Returns null on failure or timeout.
    private static async Task<string?> PollForTokenAsync(
        OperationsClient operations,
        string operationId)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var operation = (await operations.GetAsync(operationId)).Value;

            if (operation.Status == OperationStatus.Failed)
                return null;

            if (operation.Status == OperationStatus.Completed)
                return operation.Result is SshChannelOperationResult result ? result.Token : null;

            await Task.Delay(PollInterval);
        }

        return null;
    }

    private static async Task PumpStdinToSocketAsync(
        Stream stdin,
        ClientWebSocket webSocket,
        CancellationTokenSource cts)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await stdin.ReadAsync(buffer, cts.Token);
                if (read == 0)
                    break;

                await webSocket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, read),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cts.Token);
            }

            if (webSocket.State == WebSocketState.Open)
                await webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The other pump completed and cancelled us; nothing to do.
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    private static async Task PumpSocketToStdoutAsync(
        ClientWebSocket webSocket,
        Stream stdout,
        CancellationTokenSource cts)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                await stdout.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
                await stdout.FlushAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The other pump completed and cancelled us; nothing to do.
        }
        finally
        {
            await cts.CancelAsync();
        }
    }
}
