using Eryph.ClientRuntime.Configuration;
using Eryph.ComputeClient;
using Eryph.IdentityModel.Clients;

namespace Eryph.GuestServices.Client;

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
    // OAuth scopes enforced by the compute API (eryph's EryphConstants). They are
    // requested explicitly and per-call (least privilege): a restricted operator
    // client granted exactly these scopes then receives a token the server
    // accepts. The local system client is granted every scope regardless, so this
    // is a no-op for eryph-zero. The catlet lookup needs read; opening the SSH
    // channel and pushing/removing keys need remote-access.
    public const string CatletsReadScope = "compute:catlets:read";
    public const string RemoteAccessScope = "compute:catlets:remote-access";

    private EryphConnection(ClientCredentials credentials, Uri computeEndpoint)
    {
        Credentials = credentials;
        ComputeEndpoint = computeEndpoint;
    }

    // Builds a connection from credentials and a compute endpoint the caller
    // already has (e.g. an eryph app that is itself an authenticated client),
    // bypassing the on-disk configuration lookup that Resolve performs.
    public static EryphConnection For(ClientCredentials credentials, Uri computeEndpoint) =>
        new(credentials, computeEndpoint);

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

    // The catlets client is scoped per use: pass CatletsReadScope for a catlet
    // lookup and RemoteAccessScope for the SSH-channel / key routes.
    public CatletsClient CreateCatletsClient(string? scope = null) =>
        CreateClientsFactory().CreateCatletsClient(scope);

    // The operation poll only requires an authenticated user (no scope), so the
    // operations client is left unscoped.
    public OperationsClient CreateOperationsClient() => CreateClientsFactory().CreateOperationsClient();

    // Mints a bearer access token for the resolved connection. The data-plane
    // WebSocket leg passes RemoteAccessScope so the token carries the permission
    // the connect route enforces.
    public async Task<string> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null)
    {
        var response = await Credentials.GetAccessToken(scopes);
        return response.AccessToken;
    }

    // Builds an absolute API URI for a path relative to the compute endpoint,
    // including the v1 version segment, e.g. catlets/<id>/guest-services/ssh-channel/connect ->
    // https://host/compute/v1/catlets/<id>/guest-services/ssh-channel/connect.
    public Uri BuildComputeUri(string relativePath)
    {
        var baseUri = ComputeEndpoint.AbsoluteUri.TrimEnd('/');
        return new Uri($"{baseUri}/v1/{relativePath.TrimStart('/')}");
    }
}
