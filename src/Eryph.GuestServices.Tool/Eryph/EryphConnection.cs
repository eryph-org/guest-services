using Eryph.ClientRuntime.Configuration;
using Eryph.ComputeClient;
using Eryph.IdentityModel.Clients;

namespace Eryph.GuestServices.Tool.Eryph;

// Resolves the eryph connection the same way the eryph CLI / PowerShell module
// does: the configured user connection first (the default client of the
// 'default' / 'zero' / 'local' configuration), falling back to a local system
// client only when nothing else is configured. This is what makes the eryph
// command group work against a remote eryph just as well as a local eryph-zero
// -- the credential/endpoint stack resolves whatever endpoint is configured and
// nothing here is host-bound.
//
// TODO: ClientCredentialsLookup.FindCredentials() prefers the configured user
// connection but still falls back to GetSystemClientCredentials when no default
// client is configured. That fallback only succeeds for an elevated local
// caller and is the behaviour the eryph CLI itself uses, so it is acceptable
// here; there is no user-only overload in this version of the client library.
public sealed class EryphConnection
{
    private EryphConnection(ClientCredentials credentials, Uri computeEndpoint)
    {
        Credentials = credentials;
        ComputeEndpoint = computeEndpoint;
    }

    public ClientCredentials Credentials { get; }

    // Base URI of the compute API, e.g. https://localhost:8000/compute. Route
    // segments below are appended to this with the API version prefix.
    public Uri ComputeEndpoint { get; }

    // Resolves the eryph connection. With no arguments it mirrors the eryph CLI:
    // the default client of the first matching configuration, falling back to the
    // local system client (elevated callers only). When a clientId and/or
    // configurationName is given, exactly that client is resolved and the
    // system-client fallback is dropped: the operator asked for a specific
    // identity, so silently dropping to the elevated local system client would be
    // wrong. Returns null when no connection can be found; the caller turns that
    // into a user-facing error.
    public static EryphConnection? Resolve(
        string? clientId = null,
        string? configurationName = null)
    {
        var environment = new DefaultEnvironment();
        var lookup = new ClientCredentialsLookup(environment);

        ClientCredentials? credentials;
        if (clientId is not null)
            credentials = lookup.GetCredentialsByClientId(
                clientId, configurationName ?? ConfigurationNames.Default);
        else if (configurationName is not null)
            credentials = lookup.GetDefaultCredentials(configurationName);
        else
            credentials = lookup.FindCredentials();

        if (credentials is null)
            return null;

        var computeEndpoint = new EndpointLookup(environment)
            .GetEndpoint("compute", credentials.Configuration);
        if (computeEndpoint is null)
            return null;

        return new EryphConnection(credentials, computeEndpoint);
    }

    private ComputeClientsFactory CreateClientsFactory() =>
        new(new EryphComputeClientOptions(Credentials), ComputeEndpoint);

    public CatletsClient CreateCatletsClient() => CreateClientsFactory().CreateCatletsClient();

    public OperationsClient CreateOperationsClient() => CreateClientsFactory().CreateOperationsClient();

    // Mints a bearer access token for the resolved connection. The scopes are
    // requested explicitly so the channel/key routes get a token carrying the
    // remote-access permission once the server enforces it.
    public async Task<string> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null)
    {
        var response = await Credentials.GetAccessToken(scopes);
        return response.AccessToken;
    }

    // Builds an absolute API URI for a path relative to the compute endpoint,
    // including the v1 version segment, e.g. catlets/<id>/ssh-keys ->
    // https://host/compute/v1/catlets/<id>/ssh-keys.
    public Uri BuildComputeUri(string relativePath)
    {
        var baseUri = ComputeEndpoint.AbsoluteUri.TrimEnd('/');
        return new Uri($"{baseUri}/v1/{relativePath.TrimStart('/')}");
    }
}
