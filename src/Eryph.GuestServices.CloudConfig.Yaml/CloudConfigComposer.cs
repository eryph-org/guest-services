using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.CloudConfig.Yaml;

/// <summary>
/// Host-side composition of several cloud-config fragments into one document.
/// Eryph uses this to pre-merge the <c>type: cloud-config</c> fodder of a
/// catlet before building the NoCloud seed disk, rather than shipping a pile
/// of fragments for cloud-init to reconcile at runtime.
/// </summary>
/// <remarks>
/// Fragment order is precedence. Fragments are folded left-to-right with the
/// same deep-merge the guest agent applies (see RFC 0032): a later fragment
/// wins on scalar conflicts, lists are concatenated, records are deep-merged,
/// and <c>users</c>/<c>groups</c>/<c>chpasswd</c> entries merge by name. A
/// fragment can override that default with a cloud-init <c>merge_how</c> /
/// <c>merge_type</c> directive (e.g. <c>list(replace)</c>), read per fragment
/// and applied as it merges onto the accumulator.
/// </remarks>
public static class CloudConfigComposer
{
    /// <summary>
    /// Parse, deep-merge, and re-serialize <paramref name="cloudConfigFragments"/>
    /// into a single <c>#cloud-config</c> document. Empty or whitespace-only
    /// fragments are skipped. Returns <c>null</c> when there is nothing to emit.
    /// </summary>
    /// <param name="cloudConfigFragments">
    /// Cloud-config YAML fragments in precedence order (later wins on conflict).
    /// </param>
    /// <param name="onUnknownKey">
    /// Optional callback invoked once per unknown top-level key encountered in
    /// any fragment — mirrors cloud-init's warn-but-continue behaviour.
    /// </param>
    public static string? Merge(
        IEnumerable<string> cloudConfigFragments,
        Action<string>? onUnknownKey = null)
    {
        var merged = MergeToModel(cloudConfigFragments, onUnknownKey);
        return merged is null ? null : CloudConfigYamlSerializer.Serialize(merged);
    }

    /// <summary>
    /// As <see cref="Merge"/> but stops at the merged model, for callers that
    /// validate or post-process before serializing. Each fragment's
    /// <c>merge_how</c> / <c>merge_type</c> directive, if present, governs how
    /// that fragment merges onto the accumulator. Returns <c>null</c> when no
    /// non-empty fragment was supplied.
    /// </summary>
    public static CloudConfigModel? MergeToModel(
        IEnumerable<string> cloudConfigFragments,
        Action<string>? onUnknownKey = null)
    {
        ArgumentNullException.ThrowIfNull(cloudConfigFragments);

        CloudConfigModel? merged = null;
        foreach (var fragment in cloudConfigFragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
                continue;

            var parsed = CloudConfigYamlSerializer.Deserialize(fragment, onUnknownKey);
            if (merged is null)
            {
                // First fragment: nothing to merge onto, so its merge_how
                // directive (which governs how it stacks onto an accumulator)
                // does not apply yet.
                merged = parsed;
                continue;
            }

            // cloud-init semantics: the incoming fragment's merge_how / merge_type
            // controls how it merges onto the accumulated config.
            var options = MergeDirectiveReader.Read(fragment) ?? CloudInitMergeOptions.CloudInitDefault;
            merged = CloudConfigMerge.Merge(merged, parsed, options);
        }

        return merged;
    }
}
