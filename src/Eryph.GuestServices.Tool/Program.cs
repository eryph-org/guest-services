using Eryph.GuestServices.Tool.Commands;
using Eryph.GuestServices.Tool.Interceptors;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetInterceptor(new IsElevatedInterceptor());

    config.AddCommand<ProxyCommand>("proxy")
        .WithDescription(
            "Provides a proxy command for connecting to the eryph guest services with a standard SSH client.");

    config.AddCommand<ProxyCommand>("register")
        .WithDescription(
            "Registers the eryph guest services as a Hyper-V integration service.");

    config.AddCommand<ProxyCommand>("unregister")
        .WithDescription(
            "Unregisters the eryph guest services from Hyper-V.");
});

return await app.RunAsync(args);
