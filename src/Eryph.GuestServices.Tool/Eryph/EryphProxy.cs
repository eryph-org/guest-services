using System.Net.WebSockets;
using Eryph.GuestServices.Tool.Transport;

namespace Eryph.GuestServices.Tool.Eryph;

// The eryph data plane. Invoked by SSH as the ProxyCommand
// (egs-tool catlet proxy <catletId>): it opens the eryph channel (via the shared
// EryphChannel helper) and bridges the local stdin/stdout to the data-plane
// WebSocket. Protocol-agnostic: SSH runs end-to-end over the channel and eryph
// only relays the bytes. Run outside Spectre.Console.Cli so nothing touches the
// redirected stdin/stdout.
public static class EryphProxy
{
    public static async Task<int> RunAsync(
        string catletId,
        string? clientId = null,
        string? configurationName = null)
    {
        var connection = EryphConnection.Resolve(clientId, configurationName);
        if (connection is null)
        {
            await Console.Error.WriteLineAsync(
                "Could not find an eryph connection. Is eryph configured or eryph-zero running?");
            return -1;
        }

        ClientWebSocket webSocket;
        try
        {
            webSocket = await EryphChannel.OpenAsync(
                connection,
                catletId,
                // The proxy talks to ssh over stdout, so its notices go to stderr.
                writeWarning: msg => Console.Error.WriteLine($"Warning: {msg}"),
                CancellationToken.None);
        }
        catch (GuestConnectionException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return -1;
        }

        using (webSocket)
        {
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();

            using var cts = new CancellationTokenSource();
            await Task.WhenAll(
                PumpStdinToSocketAsync(stdin, webSocket, cts),
                PumpSocketToStdoutAsync(webSocket, stdout, cts));
        }

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
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            // The peer closed/aborted the socket or stdio broke during shutdown;
            // treat as end-of-stream rather than faulting the proxy.
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
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            // A normal remote close or a broken pipe to stdout surfaces here;
            // exit the pump cleanly instead of faulting the proxy.
        }
        finally
        {
            await cts.CancelAsync();
        }
    }
}
