using System.Net.WebSockets;

namespace Eryph.GuestServices.Tool.Eryph;

// The eryph data plane. Invoked by SSH as the ProxyCommand
// (egs-tool eryph proxy <catletId>): it authenticates with the operator's eryph
// connection, opens a WebSocket to the compute API ssh-channel route, and
// bridges the local stdin/stdout to that socket. Protocol-agnostic: SSH runs
// end-to-end over the channel, eryph only relays the bytes.
//
// Like the VM-level proxy this is run outside Spectre.Console.Cli so nothing
// touches the redirected stdin/stdout.
public static class EryphProxy
{
    public static async Task<int> RunAsync(string catletId)
    {
        var connection = EryphConnection.Resolve();
        if (connection is null)
        {
            await Console.Error.WriteLineAsync(
                "Could not find an eryph connection. Is eryph configured or eryph-zero running?");
            return -1;
        }

        var token = await connection.GetAccessTokenAsync();

        // Build the WebSocket URI from the compute base, switching the scheme to
        // ws/wss. The server endpoint does not exist yet; this opens the channel
        // the eryph side will implement.
        var httpUri = connection.BuildComputeUri($"catlets/{catletId}/ssh-channel");
        var wsBuilder = new UriBuilder(httpUri)
        {
            Scheme = httpUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };

        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        await webSocket.ConnectAsync(wsBuilder.Uri, CancellationToken.None);

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        using var cts = new CancellationTokenSource();
        await Task.WhenAll(
            PumpStdinToSocketAsync(stdin, webSocket, cts),
            PumpSocketToStdoutAsync(webSocket, stdout, cts));

        return 0;
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
