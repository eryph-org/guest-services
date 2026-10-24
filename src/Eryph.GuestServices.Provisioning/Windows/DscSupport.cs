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
    /// Scans <c>dsc.exe</c> JSON output for DSC v3's "restart required" signal
    /// and returns true when any resource asked for a restart. A resource
    /// surfaces this with the canonical <c>_restartRequired</c> property — an
    /// ARRAY of <c>{system|service|process}</c> objects (DSC v3.2+). DSC also
    /// collects these into the configuration result's execution information
    /// under <c>restartRequired</c>, and only emits either when at least one
    /// restart is required, so a present, non-empty array means "restart". We
    /// also honour the legacy boolean <c>_rebootRequested</c> that it replaced.
    /// Any casing, with or without the leading underscore, anywhere in the
    /// result tree, counts. Tolerant and fail-safe: malformed or non-JSON
    /// output yields <c>false</c> (no reboot).
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
                if (ContainsRebootSignal(document.RootElement))
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
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static bool ContainsRebootSignal(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsRebootKey(property.Name) && IsTruthy(property.Value))
                        return true;
                    if (ContainsRebootSignal(property.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsRebootSignal(item))
                        return true;
                }
                return false;
            default:
                return false;
        }
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
