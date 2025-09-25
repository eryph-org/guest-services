using System.Text.Json;
using System.Text.Json.Serialization;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.HvDataExchange.Host;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class GetInfoCommand : AsyncCommand<GetInfoCommand.Settings>
{
    private static readonly Lazy<JsonSerializerOptions> LazyOptions = new(() =>
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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
        guestData.TryGetValue(Constants.StatusKey, out var status);
        guestData.TryGetValue(Constants.VersionKey, out var version);
        guestData.TryGetValue(Constants.OperatingSystemKey, out var operatingSystem);

        var info = new Info
        {
            Status = string.IsNullOrEmpty(status) ? "unknown" : status,
            Version = version,
            OperatingSystem = operatingSystem,
        };

        if (settings.Json)
        {
            await AnsiConsole.Profile.Out.Writer.WriteLineAsync(
                JsonSerializer.Serialize(info, LazyOptions.Value));
            return 0;
        }

        var grid = new Grid().AddColumn().AddColumn()
            .AddRow(new Text("Status"), new Text(info.Status))
            .AddRow(new Text("Version"), new Text(info.Version ?? ""))
            .AddRow(new Text("Operating system"), new Text(info.OperatingSystem ?? ""));

        AnsiConsole.Write(grid);

        return 0;
    }

    private class Info
    {
        public required string Status { get; set; }

        public string? Version { get; set; }

        public string? OperatingSystem { get; set; }
    }
}
