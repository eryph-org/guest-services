using System.Collections.ObjectModel;
using Eryph.GuestServices.HvDataExchange.Host;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace Eryph.GuestServices.Tool.Commands;

public class InspectCommand : AsyncCommand<InspectCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        AnsiConsole.WriteLine($"VM: {settings.VmId}");
        var hostDataExchange = new HostDataExchange();
        
        var guestData = await hostDataExchange.GetGuestDataAsync(settings.VmId);
        AnsiConsole.Write(RenderGuestData(guestData));

        var intrinsicGuestData = await hostDataExchange.GetIntrinsicGuestDataAsync(settings.VmId);
        AnsiConsole.Write(RenderIntrinsicGuestData(intrinsicGuestData));

        var externalData = await hostDataExchange.GetExternalDataAsync(settings.VmId);
        AnsiConsole.Write(RenderExternalData(externalData));

        var hostOnlyData = await hostDataExchange.GetHostOnlyDataAsync(settings.VmId);
        AnsiConsole.Write(RenderHostOnlyData(hostOnlyData));

        return 0;
    }

    private IRenderable RenderGuestData(IReadOnlyDictionary<string, string> data)
    {
        var panel = new Panel(RenderData(data));
        panel.Header = new PanelHeader("Guest data");
        return panel;
    }

    private IRenderable RenderIntrinsicGuestData(IReadOnlyDictionary<string, string> data)
    {
        var panel = new Panel(RenderData(data));
        panel.Header = new PanelHeader("Intrinsic guest data");
        return panel;
    }

    private IRenderable RenderExternalData(IReadOnlyDictionary<string, string> data)
    {
        var panel = new Panel(RenderData(data));
        panel.Header = new PanelHeader("External data");
        return panel;
    }

    private IRenderable RenderHostOnlyData(IReadOnlyDictionary<string, string> data)
    {
        var panel = new Panel(RenderData(data));
        panel.Header = new PanelHeader("Host-only data");
        return panel;
    }

    private IRenderable RenderData(IReadOnlyDictionary<string, string> data)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        // Header
        grid.AddRow(new Text("Key"), new Text("Value"));

        foreach (var kvp in data.OrderBy(kvp => kvp.Key))
        {
            grid.AddRow(new Text(kvp.Key), new Text(kvp.Value));
        }

        return grid;
    }
}
