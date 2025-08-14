using Eryph.GuestServices.Sockets;
using Eryph.GuestServices.Tool.Commands;
using Eryph.GuestServices.Tool.Interceptors;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Net.Sockets;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetInterceptor(new IsElevatedInterceptor());
    config.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
        return ex.HResult;
    });

    config.SetApplicationName("egs-tool");
    config.SetApplicationVersion(GitVersionInformation.InformationalVersion);

    config.AddCommand<UploadFileCommand>("upload-file")
        .WithDescription("Uploads a file from the host to the VM.");

    config.AddCommand<GetSshKeyCommand>("get-ssh-key")
        .WithDescription(
            "Returns the public key.");

    config.AddCommand<GetStatusCommand>("get-status")
        .WithDescription(
            "Returns the status of the guest services in the VM.");

    config.AddCommand<AddSshConfigCommand>("add-ssh-config")
        .WithDescription(
            "Adds the necessary config for connecting to the given VM.");

    config.AddCommand<UpdateSshConfigCommand>("update-ssh-config")
        .WithDescription(
            "Updates the SSH config to allow connecting to the catlets.");

    config.AddCommand<InitializeCommand>("initialize")
        .WithDescription(
            "Initializes the eryph guest services on the Hyper-V host.");

    config.AddCommand<UnregisterCommand>("unregister")
        .WithDescription(
            "Unregisters the eryph guest services from Hyper-V.");
});


// The proxy command is intentionally not implemented with Spectre.Console.Cli.
// Its purpose is to forward stdin and stdout. Spectre.Console.Cli seems to interfere
// with stdin or stdout which causes the proxy to not work correctly.
// We also do not document this command as it cannot be used directly by users and
// would fail when invoked without redirecting stdin and stdout.
if (args is ["proxy", var vmId])
{
    if (!IsElevatedInterceptor.IsElevated())
        return unchecked((int)0x80070005);

    if (!Guid.TryParse(vmId, out var vmGuid))
        // Generic HResult for invalid argument
        return unchecked((int)0x80070057);

    var stdin = Console.OpenStandardInput();
    var stdout = Console.OpenStandardOutput();

    var socket = await SocketFactory.CreateClientSocket(vmGuid, Eryph.GuestServices.Core.Constants.ServiceId);
    await using var socketStream = new NetworkStream(socket, ownsSocket: true);

    await Task.WhenAll(
        stdin.CopyToAsync(socketStream),
        socketStream.CopyToAsync(stdout));

    return 0;
}

#if DEBUG
if (args is ["repl"])
{
    while (true)
    {
        Console.Write("egs-tool> ");
        var command = Console.ReadLine();
        if (string.IsNullOrEmpty(command))
            return 0;

        await app.RunAsync(command.Split(' '));
    }
}
#endif

return await app.RunAsync(args);
