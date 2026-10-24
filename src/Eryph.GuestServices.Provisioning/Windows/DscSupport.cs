using System.Security.Cryptography;
using System.Text.Json;

namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Pure, host-OS-independent helpers for the DSC v3 path. Kept out of the
/// Windows-only <see cref="WindowsOs"/> so unit tests can exercise them on any
/// host without a CA1416 platform-compatibility warning (mirrors
/// <see cref="PackageScripts"/>).
/// </summary>
internal static class DscSupport
{
    /// <summary>
    /// Scans <c>dsc config set</c> JSON output for DSC v3's "restart required"
    /// signal and returns true when a restart was requested. The signal lives in
    /// <c>executionInformation.restartRequired</c> — a list DSC emits only when a
    /// restart is required — both operation-wide at the root and per-resource
    /// under <c>results[]</c>; the deprecated <c>metadata."Microsoft.DSC"</c>
    /// location and the legacy boolean <c>_rebootRequested</c> are honoured too.
    /// A non-empty array (or a truthy boolean) counts. The lookup is deliberately
    /// scoped to these DSC-defined locations and never descends into a resource's
    /// own reported state, so a resource property that happens to be named
    /// <c>restartRequired</c> cannot trigger a spurious reboot loop. Tolerant and
    /// fail-safe: malformed or non-JSON output yields <c>false</c> (no reboot).
    /// </summary>
    public static bool RequiresReboot(string? jsonOutput)
    {
        if (string.IsNullOrWhiteSpace(jsonOutput))
            return false;

        // dsc.exe may emit one JSON document, or several concatenated / line-
        // delimited ones. Scan each top-level document we can parse and ignore
        // the rest.
        foreach (var document in EnumerateJsonDocuments(jsonOutput))
        {
            using (document)
            {
                if (RootRequestsRestart(document.RootElement))
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<JsonDocument> EnumerateJsonDocuments(string text)
    {
        var results = new List<JsonDocument>();

        // Fast path: the whole output is a single JSON document.
        if (TryParse(text, out var whole))
        {
            results.Add(whole!);
            return results;
        }

        // Fallback: treat each non-empty line as a candidate document (dsc can
        // emit JSON Lines). Lines that are not valid JSON are skipped.
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (TryParse(trimmed, out var doc))
                results.Add(doc!);
        }

        return results;
    }

    private static bool TryParse(string text, out JsonDocument? document)
    {
        try
        {
            // Raise the depth limit above the default (64) so deeply nested but
            // valid config output is still parsed rather than silently dropped.
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    // A restart counts only where DSC actually reports it: in
    // executionInformation.restartRequired (operation-wide at the root, and
    // per-resource under results[]), the deprecated metadata."Microsoft.DSC"
    // location, or the legacy boolean form on the result object itself. We never
    // descend into a resource's reported STATE — a resource that models a
    // pending-reboot setting could surface a 'restartRequired'-named property of
    // its own, and treating that as the signal would reboot-loop every set.
    private static bool RootRequestsRestart(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (RootRequestsRestart(item))
                        return true;
                return false;
            case JsonValueKind.Object:
                if (ObjectRequestsRestart(element))
                    return true;
                // Per-resource results each carry their own executionInformation.
                if (element.TryGetProperty("results", out var results))
                    return RootRequestsRestart(results);
                return false;
            default:
                return false;
        }
    }

    private static bool ObjectRequestsRestart(JsonElement obj)
    {
        // Current location: executionInformation.restartRequired (a list that
        // DSC emits only when a restart is required).
        if (obj.TryGetProperty("executionInformation", out var exec) && FlagsRestart(exec))
            return true;
        // Deprecated location (removed in DSC 4.0): metadata."Microsoft.DSC".
        if (obj.TryGetProperty("metadata", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("Microsoft.DSC", out var microsoftDsc)
            && FlagsRestart(microsoftDsc))
            return true;
        // Legacy boolean form carried directly on the result object (e.g. older
        // dsc's _rebootRequested) — the object's OWN properties only, so a
        // nested resource-state property can't trip it.
        return FlagsRestart(obj);
    }

    // True when a restart-signal property set directly on <paramref name="container"/>
    // (not nested) is present and truthy.
    private static bool FlagsRestart(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in container.EnumerateObject())
            if (IsRebootKey(property.Name) && IsTruthy(property.Value))
                return true;
        return false;
    }

    private static bool IsRebootKey(string name)
    {
        // Normalize '_restartRequired' / 'restartRequired' / '_rebootRequested'
        // and our earlier 'rebootRequired' / 'reboot_required' guess to a common
        // form. DSC's canonical property is '_restartRequired' (v3.2+), which
        // replaced the legacy boolean '_rebootRequested'.
        var normalized = name.Replace("_", "", StringComparison.Ordinal);
        return normalized.Equals("restartrequired", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("rebootrequired", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("rebootrequested", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            // '_restartRequired' / execution-info 'restartRequired' is an array of
            // restart requests; DSC emits it only when a restart is required, so a
            // non-empty array is the signal.
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var b) && b,
            JsonValueKind.Number => value.TryGetInt64(out var n) && n != 0,
            _ => false,
        };

    /// <summary>
    /// True when <paramref name="version"/> is a safe GitHub release tag token —
    /// alphanumerics plus <c>. - + _</c>, no path separators or other characters.
    /// It is interpolated into a <c>releases/tags/v{version}</c> URL, so an
    /// unvalidated value (e.g. one containing <c>/</c> or <c>..</c> segments)
    /// could redirect the request to another repository's release.
    /// </summary>
    public static bool IsValidReleaseVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && System.Text.RegularExpressions.Regex.IsMatch(
            version, @"^v?[0-9A-Za-z][0-9A-Za-z.+_-]*$");

    /// <summary>
    /// True when <paramref name="url"/> is an HTTPS GitHub release-asset URL
    /// (github.com or a *.githubusercontent.com host). The asset URL comes from
    /// release JSON, so pin it before downloading an archive we extract and run
    /// as LocalSystem.
    /// </summary>
    public static bool IsTrustedGitHubAssetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the download URL of the Windows release asset matching
    /// <paramref name="archToken"/> (e.g. <c>x86_64</c> / <c>aarch64</c>) from a
    /// GitHub release's JSON. DSC assets are named like
    /// <c>DSC-&lt;ver&gt;-x86_64-pc-windows-msvc.zip</c>. Throws
    /// <see cref="InvalidOperationException"/> when no matching asset exists.
    /// </summary>
    public static string SelectWindowsReleaseAsset(string releaseJson, string archToken)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The DSC release metadata contains no assets.");

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (url.Length == 0)
                continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains("windows", StringComparison.OrdinalIgnoreCase)
                && name.Contains(archToken, StringComparison.OrdinalIgnoreCase))
                return url;
        }

        throw new InvalidOperationException(
            $"The DSC release has no Windows '{archToken}' .zip asset.");
    }

    /// <summary>
    /// Verifies <paramref name="content"/> against a <c>sha256:&lt;hex&gt;</c>
    /// checksum spec. Returns null when the content matches, otherwise a short
    /// reason suitable for appending to an error message. A spec naming an
    /// algorithm other than sha256 is reported as unsupported (never silently
    /// accepted).
    /// </summary>
    public static string? Sha256Mismatch(byte[] content, string checksumSpec)
    {
        var spec = checksumSpec.Trim();
        var colon = spec.IndexOf(':');
        var algorithm = colon >= 0 ? spec[..colon] : "sha256";
        var expectedHex = (colon >= 0 ? spec[(colon + 1)..] : spec).Trim();

        if (!algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase))
            return $"declares an unsupported checksum algorithm '{algorithm}' (only sha256 is supported).";
        if (expectedHex.Length == 0)
            return "has an empty sha256 checksum.";

        var actualHex = Convert.ToHexString(SHA256.HashData(content));
        return actualHex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"failed its sha256 checksum (expected {expectedHex.ToLowerInvariant()}, got {actualHex.ToLowerInvariant()}).";
    }
}
