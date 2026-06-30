using Eryph.GuestServices.Client;
using Microsoft.DevTunnels.Ssh;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

// The shared shell for every file/directory transfer command: establish the
// guest SSH session through the command's transport, run the transport-neutral
// transfer, and dispose the session. Only CreateConnector (which transport) and
// TransferAsync (which copy) differ per command, so the connect, cleanup and
// error handling are written exactly once and the VM and catlet variants cannot
// drift apart.
public abstract class GuestTransferCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
    protected abstract Task<IGuestConnector> CreateConnectorAsync(TSettings settings);

    protected abstract Task<int> TransferAsync(SshSession session, TSettings settings);

    public override async Task<int> ExecuteAsync(CommandContext context, TSettings settings)
    {
        try
        {
            var connector = await CreateConnectorAsync(settings);
            await using var connection = await connector.ConnectAsync(CancellationToken.None);
            return await TransferAsync(connection.Session, settings);
        }
        catch (GuestConnectionException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return -1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error: {ex.Message}[/]");
            return -1;
        }
    }
}
