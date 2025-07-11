using System.Diagnostics;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Service.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Trace.Listeners.Add(new ConsoleTraceListener());

var builder = Host.CreateApplicationBuilder();

// TODO setup file system logging
builder.Services.AddLogging();
builder.Services.AddHostedService<SshServerService>();
builder.Services.AddSingleton<IHostKeyGenerator, HostKeyGenerator>();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IKeyStorage, WindowsKeyStorage>();
    builder.Services.AddSingleton<IGuestDataExchange, WindowsGuestDataExchange>();
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "eryph guest services";
    });
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IKeyStorage, LinuxKeyStorage>();
    builder.Services.AddSingleton<IGuestDataExchange, LinuxGuestDataExchange>();
    builder.Services.AddSystemd();
}

var host = builder.Build();

host.Run();
