﻿using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class UploadFileCommand : AsyncCommand<UploadFileCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandArgument(1, "<SourcePath>")] public string SourcePath { get; set; } = string.Empty;

        [CommandArgument(2, "<TargetPath>")] public string TargetPath { get; set; } = string.Empty;

        [CommandOption("--overwrite")] public bool Overwrite { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var keyPair = ClientKeyHelper.GetPrivateKey();
        if (keyPair is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No SSH key found. Have you run the initialize command?[/]");
            return -1;
        }
        
        var clientSocket = await SocketFactory.CreateClientSocket(settings.VmId, Constants.ServiceId);
        await using var clientStream = new NetworkStream(clientSocket, true);

        var sshConfig = new SshSessionConfiguration();
        var clientSession = new SshClientSession(sshConfig, new TraceSource("Client"));
        clientSession.Authenticating += (_, e) =>
        {
            if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
            {
                // We just trust the host as we connect via the Hyper-V socket.
                e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
            }
        };
        await clientSession.ConnectAsync(clientStream);
        await clientSession.AuthenticateAsync(new SshClientCredentials("egs", keyPair));
        // TODO session error handling

        await using var fileStream = new FileStream(settings.SourcePath, FileMode.Open, FileAccess.Read);
        var result = await clientSession.TransferFileAsync(settings.TargetPath, fileStream, settings.Overwrite, CancellationToken.None);
        
        if (unchecked((int)result) == ErrorCodes.FileExists)
            AnsiConsole.MarkupLineInterpolated($"[red]The file '{settings.TargetPath}' already exists.[/]");

        return unchecked((int)result);
    }
}
