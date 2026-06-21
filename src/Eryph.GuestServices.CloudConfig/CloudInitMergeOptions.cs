namespace Eryph.GuestServices.CloudConfig;

/// <summary>How a fragment's lists merge onto the accumulated config.</summary>
public enum ListMergeAction
{
    /// <summary>Concatenate left then right (cloud-init default).</summary>
    Append,

    /// <summary>Concatenate right then left.</summary>
    Prepend,

    /// <summary>The incoming (right) list replaces the accumulated (left) one.</summary>
    Replace,

    /// <summary>Keep the accumulated (left) list when it already has entries.</summary>
    NoReplace,
}

/// <summary>How a fragment's dictionaries merge onto the accumulated config.</summary>
public enum DictMergeAction
{
    /// <summary>Deep-merge / recurse, novel keys added (cloud-init default).</summary>
    Recurse,

    /// <summary>The incoming (right) dictionary replaces the accumulated one.</summary>
    Replace,
}

/// <summary>How a fragment's scalar strings merge onto the accumulated config.</summary>
public enum StrMergeAction
{
    /// <summary>The incoming (right) value wins (cloud-init default).</summary>
    Replace,

    /// <summary>Concatenate left + right. Parsed but not applied to typed scalars.</summary>
    Append,
}

/// <summary>
/// Parsed form of cloud-init's <c>merge_how</c> / <c>merge_type</c> directive
/// (see RFC 0032). Selects per-fragment merge behaviour for lists, dicts, and
/// strings; threaded through the source-generated <see cref="CloudConfigMerge"/>.
/// The default matches cloud-init's default merger, which is the behaviour
/// applied when a fragment carries no directive.
/// </summary>
public sealed record CloudInitMergeOptions
{
    public ListMergeAction List { get; init; } = ListMergeAction.Append;

    public DictMergeAction Dict { get; init; } = DictMergeAction.Recurse;

    public StrMergeAction Str { get; init; } = StrMergeAction.Replace;

    /// <summary>
    /// cloud-init's default merger — lists append, dicts deep-merge, scalars
    /// later-wins. Used whenever a fragment carries no merge directive.
    /// </summary>
    public static readonly CloudInitMergeOptions CloudInitDefault = new();

    /// <summary>
    /// Parse the string form of the directive, e.g.
    /// <c>list(append)+dict(no_replace,recurse_list)+str()</c>. Unknown
    /// mergers and settings are ignored permissively. Returns
    /// <see cref="CloudInitDefault"/> for null/blank input.
    /// </summary>
    public static CloudInitMergeOptions Parse(string? mergeHow)
    {
        if (string.IsNullOrWhiteSpace(mergeHow))
            return CloudInitDefault;

        var options = CloudInitDefault;
        foreach (var (name, settings) in EnumerateMergers(mergeHow!))
            options = options.WithMerger(name, settings);
        return options;
    }

    /// <summary>
    /// Apply one merger (e.g. name <c>list</c>, settings <c>append, no_replace</c>)
    /// from either the string or the structured directive form.
    /// </summary>
    public CloudInitMergeOptions WithMerger(string name, IEnumerable<string> settings)
    {
        // Settings are matched as exact tokens so "no_replace" is never seen as
        // "replace".
        var tokens = new HashSet<string>(
            settings.Select(s => s.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);

        return name.Trim().ToLowerInvariant() switch
        {
            "list" => this with { List = ParseListAction(tokens) },
            "dict" => this with { Dict = tokens.Contains("replace") ? DictMergeAction.Replace : DictMergeAction.Recurse },
            "str" => this with { Str = tokens.Contains("append") ? StrMergeAction.Append : StrMergeAction.Replace },
            _ => this,
        };
    }

    private static ListMergeAction ParseListAction(HashSet<string> tokens)
    {
        // Tokens are independent flags; if a fragment supplies contradictory
        // ones (e.g. "replace,no_replace") the most destructive wins by this
        // ordering. "no_replace" is matched as an exact token, so the earlier
        // "replace" check never captures it.
        if (tokens.Contains("replace")) return ListMergeAction.Replace;
        if (tokens.Contains("no_replace")) return ListMergeAction.NoReplace;
        if (tokens.Contains("prepend")) return ListMergeAction.Prepend;
        return ListMergeAction.Append;
    }

    // Splits "list(append)+dict(no_replace,recurse_list)+str()" into
    // (name, settings) pairs. Tolerant of whitespace and missing parentheses
    // (a bare "list" is a merger with no settings).
    private static IEnumerable<(string Name, string[] Settings)> EnumerateMergers(string mergeHow)
    {
        foreach (var raw in mergeHow.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;

            var open = part.IndexOf('(');
            if (open < 0)
            {
                yield return (part, []);
                continue;
            }

            var name = part[..open].Trim();
            var close = part.IndexOf(')', open);
            var inner = close < 0 ? part[(open + 1)..] : part[(open + 1)..close];
            var settings = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            yield return (name, settings);
        }
    }
}
