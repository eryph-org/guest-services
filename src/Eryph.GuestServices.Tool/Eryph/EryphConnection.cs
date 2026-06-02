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

    // Resolves the configured eryph connection. Returns null when no connection
    // can be found, mirroring the lookup contract; the caller turns that into a
    // user-facing error.
    public static EryphConnection? Resolve()
    {
        var environment = new DefaultEnvironment();

        var credentials = new ClientCredentialsLookup(environment).FindCredentials();
        if (credentials is null)
            return null;

        var computeEndpoint = new EndpointLookup(environment)
            .GetEndpoint("compute", credentials.Configuration);
        if (computeEndpoint is null)
            return null;

        return new EryphConnection(credentials, computeEndpoint);
    }

    public CatletsClient CreateCatletsClient()
    {
        var factory = new ComputeClientsFactory(
            new EryphComputeClientOptions(Credentials), ComputeEndpoint);
        return factory.CreateCatletsClient();
    }

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
