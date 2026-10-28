using Eryph.GuestServices.Client;
using Eryph.GuestServices.Client.ProxyTunnel;
using Eryph.GuestServices.Tool.Transport;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool catlet tunnel up --policy <file> [--identity <path>]
//
// Runs a policy-driven proxied tunnel to the catlet named in the policy file. The
// bytes are proxied in-process over the eryph-authorized channel; nothing but the
// listeners the policy declares is opened. Runs until Ctrl+C.
public sealed class CatletTunnelUpCommand : AsyncCommand<CatletTunnelUpCommand.Settings>
{
    public sealed class Settings : EryphConnectionSettings
    {
        [CommandOption("--policy <PATH>")]
        public string Policy { get; set; } = string.Empty;

        // BYOK private key for the eryph channel; omitted => the managed key.
        [CommandOption("--identity <PATH>")]
        public string? Identity { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Policy))
                return ValidationResult.Error("The --policy option is required.");

            return base.Validate();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        TunnelPolicy policy;
        try
        {
            policy = TunnelPolicyLoader.LoadFromFile(settings.Policy);
        }
        catch (TunnelPolicyException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return -1;
        }

        IGuestConnector connector;
        try
        {
            connector = await ToolGuestConnectors.CreateEryphAsync(
                policy.Catlet,
                settings.ClientId,
                settings.Configuration,
                settings.Identity,
                writeWarning: message => AnsiConsole.MarkupLine($"[yellow]{message.EscapeMarkup()}[/]"),
                keepAliveTimeoutSeconds: policy.Keepalive.IntervalSeconds);
        }
        catch (GuestConnectionException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return -1;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // First Ctrl+C: stop the tunnel cleanly rather than killing the process.
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        var engine = new ProxyTunnelEngine(
            connector,
            policy,
            writeInfo: message => AnsiConsole.MarkupLine($"[grey]{message.EscapeMarkup()}[/]"),
            writeWarning: message => AnsiConsole.MarkupLine($"[yellow]{message.EscapeMarkup()}[/]"));

        try
        {
            await engine.RunAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine($"[grey]Tunnel '{policy.Tunnel.EscapeMarkup()}' stopped.[/]");
            return 0;
        }
        catch (TunnelPolicyException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return -1;
        }
        catch (GuestConnectionException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return -1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
