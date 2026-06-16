namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>
/// Parses a GNU-coreutils <c>SHA256SUMS</c> file: one
/// <c>&lt;hex&gt;␠␠&lt;filename&gt;</c> line per artifact. This is the
/// signed authority for the package hashes.
/// </summary>
public sealed class Sha256Sums
{
    private readonly IReadOnlyDictionary<string, string> hashesByFile;

    private Sha256Sums(IReadOnlyDictionary<string, string> hashesByFile) =>
        this.hashesByFile = hashesByFile;

    public static Sha256Sums Parse(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            // "<hash><space><space-or-*><filename>" — split on the first run of
            // whitespace; the marker char ('*' for binary mode, ' ' for text)
            // is stripped from the filename.
            var sep = line.IndexOf(' ');
            if (sep <= 0)
                continue;

            var hash = line[..sep];
            var file = line[(sep + 1)..].TrimStart(' ', '*');
            if (hash.Length > 0 && file.Length > 0)
                map[file] = hash.ToLowerInvariant();
        }

        return new Sha256Sums(map);
    }

    /// <summary>The signed SHA256 for <paramref name="fileName"/>, or null when absent.</summary>
    public string? GetHash(string fileName) => hashesByFile.GetValueOrDefault(fileName);
}
