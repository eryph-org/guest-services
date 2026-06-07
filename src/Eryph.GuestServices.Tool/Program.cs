using Eryph.GuestServices.Sockets;
using Eryph.GuestServices.Tool;
using Eryph.GuestServices.Tool.Commands;
using Eryph.GuestServices.Tool.Commands.Eryph;
using Eryph.GuestServices.Tool.Eryph;
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

    config.AddCommand<UploadDirectoryCommand>("upload-directory")
        .WithDescription("Uploads a directory from the host to the VM.");

    config.AddCommand<DownloadFileCommand>("download-file")
        .WithDescription("Downloads a file from the VM to the host.");

    config.AddCommand<DownloadDirectoryCommand>("download-directory")
        .WithDescription("Downloads a directory from the VM to the host.");

    config.AddCommand<GetSshKeyCommand>("get-ssh-key")
        .WithDescription(
            "Returns the public key.");

    config.AddCommand<GetStatusCommand>("get-status")
        .WithDescription(
            "Returns the status of the guest services in the VM.");

    config.AddCommand<GetDataCommand>("get-data")
        .WithDescription(
            "Returns the key-value data which is associated with the VM.");

    config.AddCommand<AddSshConfigCommand>("add-ssh-config")
        .WithDescription(
            "Adds the necessary config for connecting to the given VM.");

    config.AddCommand<InitializeCommand>("initialize")
        .WithDescription(
            "Initializes the eryph guest services on the Hyper-V host.");

    config.AddCommand<UnregisterCommand>("unregister")
        .WithDescription(
            "Unregisters the eryph guest services from Hyper-V.");

    config.AddCommand<SetShellCommand>("set-shell")
        .WithDescription(
            "Configures the shell that interactive SSH sessions spawn in the VM.");

    config.AddBranch("catlet", eryph =>
    {
        eryph.SetDescription(
            "Remote access to eryph catlets through the eryph-authorized channel.");

        eryph.AddCommand<EryphAddSshConfigCommand>("add-ssh-config")
            .WithDescription(
                "Adds an SSH config alias for connecting to the given catlet via eryph.");

        eryph.AddCommand<EryphGetClientKeyCommand>("get-client-key")
            .WithDescription(
                "Prints the managed client public key for pre-injecting into a catlet.");

        eryph.AddCommand<EryphAddKeyCommand>("add-key")
            .WithDescription(
                "Pushes a public key to the given catlet's guest via eryph.");

        eryph.AddCommand<EryphRemoveKeyCommand>("remove-key")
            .WithDescription(
                "Revokes the caller's key on the given catlet via eryph.");

        eryph.AddCommand<EryphGetStatusCommand>("get-status")
            .WithDescription(
                "Prints the guest services and provisioning status of the given catlet.");

        eryph.AddCommand<EryphSetShellCommand>("set-shell")
            .WithDescription(
                "Sets the SSH session shell on the given catlet via eryph.");
    });
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

// The catlet data-plane proxy is, like the VM-level proxy above, kept out of
// Spectre.Console.Cli so nothing interferes with the redirected stdin/stdout it
// bridges to the eryph channel. Unlike the VM proxy it must NOT require host
// admin: it runs on the operator's machine and authenticates with the
// operator's eryph identity.
if (args.Length >= 3 && args[0] == "catlet" && args[1] == "proxy")
{
    var catletId = args[2];
    // Validate the id even though the generated alias only ever writes a safe one:
    // this entry point can be invoked directly, and the Spectre.Console.Cli
    // validation layer is bypassed here. Mirrors the VM-proxy GUID check above.
    if (!SshConfigHelper.IsSafeHostToken(catletId))
    {
        await Console.Error.WriteLineAsync("Invalid catlet id.");
        return unchecked((int)0x80070057);
    }

    // The generated ssh_config alias may append the operator's connection
    // selectors (see SshConfigHelper.WriteCatletConfig). Parse them here so the
    // proxy resolves the same client/configuration; parsing stays manual because
    // this branch deliberately bypasses Spectre.Console.Cli.
    string? proxyConfiguration = null;
    string? proxyClientId = null;
    for (var i = 3; i < args.Length; i++)
    {
        var option = args[i];
        if (option is "--configuration" or "--client-id")
        {
            // Fail loudly on a flag without a value: silently falling back to the
            // default connection would run the proxy under the wrong identity.
            if (i + 1 >= args.Length)
            {
                await Console.Error.WriteLineAsync($"Missing value for {option}.");
                return unchecked((int)0x80070057);
            }

            var value = args[++i];
            // Apply the same safe-token rule the CLI settings layer enforces, so a
            // direct invocation cannot smuggle an unsafe selector past validation.
            if (!SshConfigHelper.IsSafeHostToken(value))
            {
                await Console.Error.WriteLineAsync($"Invalid value for {option}.");
                return unchecked((int)0x80070057);
            }

            if (option == "--configuration")
                proxyConfiguration = value;
            else
                proxyClientId = value;
        }
        else
        {
            await Console.Error.WriteLineAsync($"Unknown proxy argument '{option}'.");
            return unchecked((int)0x80070057);
        }
    }

    return await EryphProxy.RunAsync(catletId, proxyClientId, proxyConfiguration);
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
