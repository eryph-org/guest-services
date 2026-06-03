using Eryph.GuestServices.Tool.Interceptors;
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
}
