using System.ComponentModel;
using System.Text.Json;
using Eryph.GuestServices.Provisioning.State;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Reads <c>state.json</c> and prints the agent's current status. Always
/// reads from disk (never from an in-memory store) so the answer reflects
/// what the next agent boot will see.
/// </summary>
public sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(60);

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--wait")]
        [Description("Block until provisioning reaches a terminal state.")]
        public bool Wait { get; init; }

        [CommandOption("--json")]
        [Description("Emit raw JSON instead of a human-readable summary.")]
        public bool Json { get; init; }

        [CommandOption("--state-dir <DIR>")]
        [Description("Override the state directory (default: %ProgramData%\\eryph\\provisioning).")]
        public string? StateDir { get; init; }
    }

    private readonly IStateStore _store;

    public StatusCommand()
        : this(new FileStateStore(NullLogger<FileStateStore>.Instance))
    {
    }

    // Internal hook for tests.
    internal StatusCommand(IStateStore store)
    {
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.StateDir))
            ProvisioningPaths.RootOverride = settings.StateDir;

        if (settings.Wait)
        {
            var deadline = DateTime.UtcNow + WaitTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var state = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                if (IsTerminal(state))
                {
                    Print(state, settings.Json);
                    return state is null ? 0 : 0;
                }
                await Task.Delay(WaitPollInterval).ConfigureAwait(false);
            }
            AnsiConsole.MarkupLine("[yellow]Timed out waiting for provisioning to complete.[/]");
            return 3;
        }

        var current = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Print(current, settings.Json);
        return 0;
    }

    private static bool IsTerminal(ProvisioningState? state)
    {
        if (state is null) return false;
        // Final stage written = all stages done. Or instance with no progress
        // (unlikely but acceptable). We treat completion of the Final stage as
        // the terminal marker because that's what StageRunner writes last.
        return state.CompletedStages.Contains("Final");
    }

    private static void Print(ProvisioningState? state, bool json)
    {
        if (json)
        {
            var payload = state ?? new ProvisioningState();
            AnsiConsole.WriteLine(JsonSerializer.Serialize(payload, StateStoreJsonContext.Default.ProvisioningState));
            return;
        }

        if (state is null)
        {
            AnsiConsole.MarkupLine("[grey]No state file found — agent has not run yet.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("");
        table.AddColumn("");
        table.AddRow("Instance", state.InstanceId);
        table.AddRow("Started", state.StartedAt == default ? "-" : state.StartedAt.ToString("u"));
        table.AddRow("Last updated", state.LastUpdated == default ? "-" : state.LastUpdated.ToString("u"));
        table.AddRow("Reboots", state.RebootCount.ToString());
        table.AddRow("Completed stages",
            state.CompletedStages.Count == 0 ? "-" : string.Join(", ", state.CompletedStages));
        table.AddRow("Completed modules",
            state.CompletedHandlers.Count == 0 ? "-" : string.Join("\n", state.CompletedHandlers));
        var summary = state.CompletedStages.Contains("Final") ? "completed" : "in progress";
        table.AddRow("Current state", summary);
        AnsiConsole.Write(table);
    }
}
