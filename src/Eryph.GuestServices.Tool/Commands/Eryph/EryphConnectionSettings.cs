using Eryph.GuestServices.Tool.Interceptors;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// Base settings for the 'eryph' command group: every command in this group talks
// to the eryph compute API with the operator's eryph identity, so it shares the
// connection selectors and the elevation exemption. Omitting both selectors keeps
// the eryph-CLI default behaviour (default client of the configured connection).
public class EryphConnectionSettings : CommandSettings, IElevationExempt
{
    // Selects a named eryph configuration (e.g. "default", "zero", "local").
    // Omitted => the first configuration the eryph CLI would pick.
    [CommandOption("--configuration <NAME>")]
    public string? Configuration { get; set; }

    // Selects a specific eryph client by id within the configuration. Omitted =>
    // the configuration's default client. Supplying either selector also disables
    // the elevated local system-client fallback.
    [CommandOption("--client-id <CLIENTID>")]
    public string? ClientId { get; set; }

    // Both selectors are identifiers (a configuration name or a client id) and are
    // embedded verbatim into the generated ProxyCommand line. Constrain them to a
    // safe character set so a value can never break out of its quoted argument or
    // inject shell tokens; this removes the need for fragile cross-shell escaping.
    public override ValidationResult Validate()
    {
        foreach (var (name, value) in new[] { ("--configuration", Configuration), ("--client-id", ClientId) })
        {
            if (value is not null && !IsSafeSelector(value))
                return ValidationResult.Error(
                    $"The value for {name} may only contain letters, digits, '.', '-' and '_'.");
        }

        return ValidationResult.Success();
    }

    private static bool IsSafeSelector(string value) =>
        value.Length > 0
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
