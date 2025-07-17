using Eryph.GuestServices.Tool.Commands;
using Eryph.GuestServices.Tool.Interceptors;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetInterceptor(new IsElevatedInterceptor());
    config.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
        return ex.HResult;
    });

    config.AddCommand<UploadFileCommand>("upload-file")
        .WithDescription("Uploads a file from the host to the VM.");

    config.AddCommand<GetSshKeyCommand>("get-ssh-key")
        .WithDescription(
            "Returns the public key.");

    config.AddCommand<GetStatusCommand>("get-status")
        .WithDescription(
            "Returns the status of the guest services in the VM.");

    config.AddCommand<UpdateSshConfigCommand>("update-ssh-config")
        .WithDescription(
            "Updates the SSH config to allow connecting to the catlets.");

    config.AddCommand<ProxyCommand>("proxy")
        .WithDescription(
            "Provides a proxy command for connecting to the eryph guest services with a standard SSH client.");

    config.AddCommand<InitializeCommand>("initialize")
        .WithDescription(
            "Initializes the eryph guest services on the Hyper-V host.");

    config.AddCommand<UnregisterCommand>("unregister")
        .WithDescription(
            "Unregisters the eryph guest services from Hyper-V.");
});

if (args.Length != 1 || args[0] != "repl")
    return await app.RunAsync(args);

while (true)
{
    Console.Write("egs-tool> ");
    var command = Console.ReadLine();
    if (string.IsNullOrEmpty(command))
        return 0;

    await app.RunAsync(command.Split(' '));
}
