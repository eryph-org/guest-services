using System.Net;
using Eryph.GuestServices.OpenStackMetadataSim;

// Minimal OpenStack metadata-service simulator for e2e: serves a captured
// config-2 tree (openstack/<version>/…) over HTTP with the /openstack version
// listing, so the egs OpenStack datasource can be exercised against real
// fixtures. Bind it to the link-local 169.254.169.254 (or any test address).
//
//   egs-openstack-sim --root /srv/metadata --prefix http://+:80/

var root = GetArg(args, "--root")
           ?? Environment.GetEnvironmentVariable("EGS_SIM_ROOT")
           ?? Directory.GetCurrentDirectory();
var prefix = GetArg(args, "--prefix")
             ?? Environment.GetEnvironmentVariable("EGS_SIM_PREFIX")
             ?? "http://+:80/";

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"root directory does not exist: {root}");
    return 1;
}

var responder = new MetadataResponder(root);

using var listener = new HttpListener();
listener.Prefixes.Add(prefix);
listener.Start();
Console.WriteLine($"OpenStack metadata sim: serving '{root}' on '{prefix}'");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    // GetContextAsync blocks until the next request; stopping the listener
    // unblocks it (throwing below) so shutdown is prompt rather than waiting
    // for one more HTTP request to arrive.
    try { listener.Stop(); } catch { /* already stopping */ }
};

try
{
    while (!cts.IsCancellationRequested)
    {
        var context = await listener.GetContextAsync().ConfigureAwait(false);
        _ = Task.Run(() => HandleAsync(context, responder));
    }
}
catch (Exception ex) when (cts.IsCancellationRequested
                           && ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
{
    // listener stopped during shutdown — expected
}

return 0;

static async Task HandleAsync(HttpListenerContext context, MetadataResponder responder)
{
    try
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var response = responder.Respond(path);

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength64 = response.Body.Length;
        Console.WriteLine($"{context.Request.HttpMethod} {path} -> {response.StatusCode} ({response.Body.Length} bytes)");

        if (response.Body.Length > 0)
            await context.Response.OutputStream.WriteAsync(response.Body).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error handling request: {ex.Message}");
    }
    finally
    {
        context.Response.Close();
    }
}

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
            return args[i + 1];
    }
    return null;
}
