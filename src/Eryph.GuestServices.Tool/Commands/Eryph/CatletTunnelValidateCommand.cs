using Eryph.GuestServices.Client.ProxyTunnel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool catlet tunnel validate --policy <file>
//
// Loads and validates a policy file without connecting to anything. Reports every
// problem found, so a malformed or over-broad policy is caught before it is run.
public sealed class CatletTunnelValidateCommand : AsyncCommand<CatletTunnelValidateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--policy <PATH>")]
        public string Policy { get; set; } = string.Empty;

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(Policy)
                ? ValidationResult.Error("The --policy option is required.")
                : ValidationResult.Success();
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var policy = TunnelPolicyLoader.LoadFromFile(settings.Policy);
            AnsiConsole.MarkupLine(
                $"[green]Policy '{policy.Tunnel.EscapeMarkup()}' is valid: "
                + $"{policy.Egress.Count} egress, {policy.Ingress.Count} ingress rule(s).[/]");
            return Task.FromResult(0);
        }
        catch (TunnelPolicyException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return Task.FromResult(-1);
        }
    }
}
