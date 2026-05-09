using System.Diagnostics;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Service.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Trace.Listeners.Add(new ConsoleTraceListener());

// TEMPORARY: capture every outgoing/incoming SSH message for the cmd.exe
// post-close-data investigation. Writes to C:\egs-staging\ssh-trace.log.
var sshTraceFile = OperatingSystem.IsWindows()
    ? @"C:\egs-staging\ssh-trace.log"
    : "/tmp/ssh-trace.log";
try
{
    var dir = Path.GetDirectoryName(sshTraceFile);
    if (dir is not null) Directory.CreateDirectory(dir);
    var listener = new TextWriterTraceListener(sshTraceFile, "ssh") { TraceOutputOptions = TraceOptions.DateTime };
    Trace.AutoFlush = true;
    Trace.Listeners.Add(listener);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed to attach ssh trace listener: {ex.Message}");
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings()
{
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddLogging();
builder.Services.AddHostedService<SshServerService>();
builder.Services.AddSingleton<IHostKeyGenerator, HostKeyGenerator>();
builder.Services.AddSingleton<IClientKeyProvider, ClientKeyProvider>();
builder.Services.AddSingleton<IShellSelector, KvpShellSelector>();

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

host.Services.GetRequiredService<ILogger<Program>>().LogInformation(
    "Starting eryph guest services {Version}...",
    GitVersionInformation.InformationalVersion);

await host.RunAsync();
