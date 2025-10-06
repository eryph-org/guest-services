using System.Text.Json;
using System.Text.Json.Serialization;
using Eryph.GuestServices.HvDataExchange.Host;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace Eryph.GuestServices.Tool.Commands;

public class GetDataCommand : AsyncCommand<GetDataCommand.Settings>
{
    private static readonly Lazy<JsonSerializerOptions> LazyOptions = new(() =>
        new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        });

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandOption("--json")] public bool Json { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var hostDataExchange = new HostDataExchange();
        
        var guestData = await hostDataExchange.GetGuestDataAsync(settings.VmId);
        var intrinsicGuestData = await hostDataExchange.GetIntrinsicGuestDataAsync(settings.VmId);
        var externalData = await hostDataExchange.GetExternalDataAsync(settings.VmId);
        var hostOnlyData = await hostDataExchange.GetHostOnlyDataAsync(settings.VmId);

        if (settings.Json)
        {
            var allData = new Dictionary<string, IDictionary<string, JsonElement>>
            {
                ["guest"] = ConvertToJson(guestData),
                ["guest_intrinsic"] = ConvertToJson(intrinsicGuestData),
                ["external"] = ConvertToJson(externalData),
                ["host_only"] = ConvertToJson(hostOnlyData),
            };
            var json = JsonSerializer.Serialize(allData, LazyOptions.Value);
            AnsiConsole.WriteLine(json);
            return 0;
        }

        AnsiConsole.Write(RenderData("Guest data", guestData));
        AnsiConsole.Write(RenderData("Intrinsic guest data", intrinsicGuestData));
        AnsiConsole.Write(RenderData("External data", externalData));
        AnsiConsole.Write(RenderData("Host-only data", hostOnlyData));

        return 0;
    }

    private static IDictionary<string, JsonElement> ConvertToJson(
        IReadOnlyDictionary<string, string> data)
    {
        // Some guest utilities (e.g. cloud-init) use JSON in the values.
        // We try to detect and parse this JSON. This way, we avoid having
        // escaped JSON in the final result.
        return data
            .Select(kvp => (kvp.Key, Value: ConvertToJson(kvp.Value)))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static JsonElement ConvertToJson(string data)
    {
        if (!data.TrimStart().StartsWith('{'))
            return JsonSerializer.SerializeToElement(data);

        try
        {
            var document = JsonDocument.Parse(data, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Return the original string when the JSON cannot be parsed
            return JsonSerializer.SerializeToElement(data);
        }
    }

    private IRenderable RenderData(string header, IReadOnlyDictionary<string, string> data)
    {
        if (data.Count == 0)
        {
            return new Panel(new Text("no data"))
            {
                Header = new PanelHeader(header),
                Expand = true,
            };
        }

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        // Header
        grid.AddRow(new Text("Key"), new Text("Value"));

        foreach (var kvp in data.OrderBy(kvp => kvp.Key))
        {
            grid.AddRow(new Text(kvp.Key), new Text(kvp.Value));
        }

        return new Panel(grid)
        {
            Header = new PanelHeader(header),
            Expand = true,
        };
    }
}
