using Eryph.GuestServices.Core;
using Eryph.GuestServices.Sockets;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Eryph.GuestServices.Tool.Commands;

public class ProxyCommand : AsyncCommand<ProxyCommand.Settings>
{

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }
    }
    
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // TODO how should cancellation work?
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();


        var socket = await SocketFactory.CreateClientSocket(settings.VmId, Constants.ServiceId);
        await using var socketStream = new NetworkStream(socket, ownsSocket: true);

        var stdinTask = Task.Run(async () =>
        {
            // TODO Investigate buffer size
            var buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = await stdin.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await socketStream.WriteAsync(buffer, 0, bytesRead);
            }
        });

        var stdoutTask = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = await socketStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await stdout.WriteAsync(buffer, 0, bytesRead);
            }
        });

        await Task.WhenAll(stdinTask, stdoutTask);
        return 0;
    }
}
