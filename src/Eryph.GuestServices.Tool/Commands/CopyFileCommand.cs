using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Tool.Commands;

public class CopyFileCommand : AsyncCommand<CopyFileCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandArgument(1, "<SourcePath>")] public string SourcePath { get; set; } = string.Empty;

        [CommandArgument(2, "<TargetPath>")] public string TargetPath { get; set; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var keyPair = GetPrivateKey();
        if (keyPair is null)
            return -1; 

        var clientSocket = await SocketFactory.CreateClientSocket(settings.VmId, Constants.ServiceId);
        await using var clientStream = new NetworkStream(clientSocket, true);

        var sshConfig = new SshSessionConfiguration();
        var clientSession = new SshClientSession(sshConfig, new TraceSource("Client"));
        clientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        await clientSession.ConnectAsync(clientStream);
        await clientSession.AuthenticateAsync(new SshClientCredentials("egs", keyPair));

        await using var fileStream = new FileStream(settings.SourcePath, FileMode.Open, FileAccess.Read);

        await clientSession.TransferFileAsync(settings.TargetPath, fileStream, CancellationToken.None);
        return 0;
    }

    private IKeyPair? GetPrivateKey()
    {
        var keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "guest-services",
            "private",
            "id_egs");

        if (!Path.Exists(keyFilePath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No SSH key found. Have you run the initialize command?[/]");
            return null;
        }

        return KeyPair.ImportKeyFile(keyFilePath);
    }
}
