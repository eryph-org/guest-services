using System.Net.Http.Json;
using Eryph.GuestServices.Tool.Eryph;
using Eryph.GuestServices.Tool.Interceptors;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool eryph add-key <catletId> [--public-key <path|->] [--ttl <duration>]
//
// Pushes a public key to the catlet's guest via eryph (the runtime flow). The
// key is read from --public-key (a path, or "-" for stdin) or the managed key
// when omitted, and POSTed to the compute API key-install route.
public class EryphAddKeyCommand : AsyncCommand<EryphAddKeyCommand.Settings>
{
    public class Settings : CommandSettings, IElevationExempt
    {
        [CommandArgument(0, "<CatletId>")]
        public string CatletId { get; set; } = string.Empty;

        // A path to a public key, "-" to read it from stdin, or omitted to use
        // the managed client key.
        [CommandOption("--public-key <PATH>")]
        public string? PublicKey { get; set; }

        // Optional time-to-live, e.g. "8h", "30m", "1d12h". Omitted => the
        // server applies its default / no expiry.
        [CommandOption("--ttl <DURATION>")]
        public string? Ttl { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var connection = EryphConnection.Resolve();
        if (connection is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Could not find an eryph connection. Is eryph configured or eryph-zero running?[/]");
            return -1;
        }

        return await PushKeyAsync(connection, settings.CatletId, settings.PublicKey, settings.Ttl);
    }

    // Shared by add-key and add-ssh-config --add-key. Resolves the public key,
    // parses the TTL into an absolute expiry, and POSTs the install request.
    public static async Task<int> PushKeyAsync(
        EryphConnection connection,
        string catletId,
        string? publicKeyOption,
        string? ttl)
    {
        var publicKey = await PublicKeyReader.ResolveAsync(publicKeyOption);
        if (string.IsNullOrEmpty(publicKey))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Could not read a public key. Provide --public-key or run 'initialize' for a managed key.[/]");
            return -1;
        }

        TimeSpan? duration = null;
        DateTimeOffset? expiresAt = null;
        if (!string.IsNullOrEmpty(ttl))
        {
            if (!DurationParser.TryParse(ttl, out var parsed))
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]Invalid --ttl '{ttl}'. Use a duration such as 8h, 30m or 1d12h.[/]");
                return -1;
            }

            duration = parsed;
            expiresAt = DateTimeOffset.UtcNow.Add(parsed);
        }

        using var httpClient = new HttpClient();
        var token = await connection.GetAccessTokenAsync();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var uri = connection.BuildComputeUri($"catlets/{catletId}/ssh-keys");
        var body = new SshKeyInstallRequest
        {
            PublicKey = publicKey,
            // Send the TTL as an ISO 8601 duration so the value is unambiguous,
            // plus the absolute expiry computed locally.
            Ttl = duration is { } d ? System.Xml.XmlConvert.ToString(d) : null,
            ExpiresAt = expiresAt,
        };

        var response = await httpClient.PostAsJsonAsync(uri, body);
        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Failed to add the key ({(int)response.StatusCode} {response.ReasonPhrase}).[/]");
            return -1;
        }

        if (expiresAt is { } e)
            AnsiConsole.MarkupLineInterpolated(
                $"[green]The key was added and expires at {e.UtcDateTime:u}.[/]");
        else
            AnsiConsole.MarkupLine("[green]The key was added.[/]");

        return 0;
    }

    // JSON body for POST /v1/catlets/{id}/ssh-keys. The compute route does not
    // exist in the generated client yet, so it is called with a raw HttpClient.
    private sealed class SshKeyInstallRequest
    {
        public string PublicKey { get; init; } = string.Empty;

        public string? Ttl { get; init; }

        public DateTimeOffset? ExpiresAt { get; init; }
    }
}
