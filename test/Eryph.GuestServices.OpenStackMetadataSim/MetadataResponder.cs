using System.Text;

namespace Eryph.GuestServices.OpenStackMetadataSim;

/// <summary>The status, body and content type to return for one request.</summary>
public sealed record MetadataResponse(int StatusCode, byte[] Body, string ContentType);

/// <summary>
/// Pure request router for the OpenStack metadata service contract, served from
/// a static directory tree (e.g. the captured config-2 <c>openstack/&lt;v&gt;/…</c>
/// fixture). Kept free of any socket/HttpListener concerns so it can be unit
/// tested directly. The HttpListener host (Program) is a thin shell over it.
///
/// Contract (spec §1): <c>GET /openstack</c> returns the newline-separated
/// version directory listing (the liveness + version-list endpoint); everything
/// else is a static file read, with 404 for anything absent.
/// </summary>
public sealed class MetadataResponder
{
    private readonly string _root;

    public MetadataResponder(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
    }

    public MetadataResponse Respond(string absolutePath)
    {
        var rel = (absolutePath ?? string.Empty).Trim('/');

        // The version listing IS the liveness probe and the version selector.
        if (string.Equals(rel, "openstack", StringComparison.Ordinal))
            return VersionList();

        var file = ResolveWithinRoot(rel);
        if (file is null || !File.Exists(file))
            return NotFound();

        return new MetadataResponse(200, File.ReadAllBytes(file), ContentTypeFor(file));
    }

    private MetadataResponse VersionList()
    {
        var openstackDir = Path.Combine(_root, "openstack");
        if (!Directory.Exists(openstackDir))
            return NotFound();

        // Newline-separated directory names (blank lines are stripped by the
        // client). cloud-init conventionally includes `latest`; we list exactly
        // what's on disk so the sim is a faithful mirror of the captured tree.
        var versions = Directory.EnumerateDirectories(openstackDir)
            .Select(d => Path.GetFileName(d)!)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.Ordinal);

        var body = string.Join("\n", versions);
        return new MetadataResponse(200, Encoding.UTF8.GetBytes(body), "text/plain; charset=utf-8");
    }

    // Maps a forward-slash relative path to a file under the root, refusing any
    // path that would escape it (defence in depth — the sim only serves fixtures).
    private string? ResolveWithinRoot(string rel)
    {
        if (string.IsNullOrEmpty(rel))
            return null;

        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            return null;

        var combined = Path.GetFullPath(Path.Combine([_root, .. segments]));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (combined != _root && !combined.StartsWith(rootWithSep, StringComparison.Ordinal))
            return null;

        return combined;
    }

    private static string ContentTypeFor(string file) =>
        file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? "application/json"
            : "application/octet-stream";

    private static MetadataResponse NotFound() =>
        new(404, [], "text/plain; charset=utf-8");
}
