using System.Net.Sockets;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Sockets;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class ProxyCommand : AsyncCommand<ProxyCommand.Settings>
{

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }
    }
    
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        var socket = await SocketFactory.CreateClientSocket(settings.VmId, Constants.ServiceId);
        await using var socketStream = new NetworkStream(socket, ownsSocket: true);

        await Task.WhenAll(
            stdin.CopyToAsync(socketStream),
            socketStream.CopyToAsync(stdout));

        return 0;
    }
}
