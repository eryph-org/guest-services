namespace Eryph.GuestServices.CloudConfig;

// Deep-merge policy for stacking cloud-config fragments coming from
// multipart parts and #include URLs:
//
//   - scalar values:   right (incoming) overrides left (accumulated)
//                      when the right side is non-null
//   - lists:           concatenated (left, then right) preserving order,
//                      EXCEPT users/groups/chpasswd-users which merge by
//                      Name with the right entry replacing the left
//   - dicts (records): deep-merged field by field
//
// The "users replace by name" twist matches cloud-init's behaviour: two
// fragments declaring the same user name produce a single user record,
// with later declarations winning on conflict.
//
// A per-fragment cloud-init merge_how / merge_type directive (RFC 0032) is
// carried as a CloudInitMergeOptions and threaded through every helper so a
// fragment can opt into list replace/prepend/no_replace or dict replace.
// CloudInitMergeOptions.CloudInitDefault reproduces the behaviour above, so
// the parameterless callers and the no-directive path are unchanged.
//
// The per-property merge code is source-generated from the model's
// [CloudInitRoot] / [CloudInitRecord] / [CloudInitField] / [MergeBehavior]
// attributes — see Eryph.GuestServices.CloudConfig.SourceGen. The hand-
// written helpers below stay here so generated code can call them by name:
// they handle the cloud-init specific merge semantics (deep-merge users
// keyed by Name, concat lists, etc.) that are not pure per-property
// scalar overrides.
public static partial class CloudConfigMerge
{
    /// <summary>
    /// Deep-merge two cloud-configs using cloud-init's default merger. The
    /// source-generated <c>Merge(left, right, options)</c> overload carries
    /// the per-fragment <see cref="CloudInitMergeOptions"/>; this is the
    /// convenience entry point for callers that do not vary the strategy.
    /// </summary>
    public static CloudConfig Merge(CloudConfig left, CloudConfig right) =>
        Merge(left, right, CloudInitMergeOptions.CloudInitDefault);

    private static UserConfig MergeUser(UserConfig left, UserConfig right, CloudInitMergeOptions options) => new()
    {
        Name = right.Name ?? left.Name,
        Passwd = right.Passwd ?? left.Passwd,
        PlainTextPasswd = right.PlainTextPasswd ?? left.PlainTextPasswd,
        HashedPasswd = right.HashedPasswd ?? left.HashedPasswd,
        LockPasswd = right.LockPasswd ?? left.LockPasswd,
        Groups = Concat(left.Groups, right.Groups, options),
        SshAuthorizedKeys = Concat(left.SshAuthorizedKeys, right.SshAuthorizedKeys, options),
        Inactive = right.Inactive ?? left.Inactive,
        Shell = right.Shell ?? left.Shell,
        HomeDir = right.HomeDir ?? left.HomeDir,
        PrimaryGroup = right.PrimaryGroup ?? left.PrimaryGroup,
        // Sudo widened from string? to IReadOnlyList<string>? — cloud-init
        // accepts a single string OR a list of strings, and stacking two
        // fragments that each carry sudoers lines concatenates them.
        Sudo = Concat(left.Sudo, right.Sudo, options),
        System = right.System ?? left.System,
        Gecos = right.Gecos ?? left.Gecos,
        SshImportId = Concat(left.SshImportId, right.SshImportId, options),
        SshRedirectUser = right.SshRedirectUser ?? left.SshRedirectUser,
        Expiredate = right.Expiredate ?? left.Expiredate,
        NoCreateHome = right.NoCreateHome ?? left.NoCreateHome,
        NoUserGroup = right.NoUserGroup ?? left.NoUserGroup,
        NoLogInit = right.NoLogInit ?? left.NoLogInit,
        SelinuxUser = right.SelinuxUser ?? left.SelinuxUser,
        Uid = right.Uid ?? left.Uid,
        Snapuser = right.Snapuser ?? left.Snapuser,
    };

    private static GroupConfig MergeGroup(GroupConfig left, GroupConfig right, CloudInitMergeOptions options) => new()
    {
        Name = right.Name ?? left.Name,
        Members = Concat(left.Members, right.Members, options),
        Gid = right.Gid ?? left.Gid,
    };

    private static ChpasswdListEntry MergeChpasswdEntry(ChpasswdListEntry left, ChpasswdListEntry right, CloudInitMergeOptions options) => new()
    {
        Name = right.Name ?? left.Name,
        Password = right.Password ?? left.Password,
        Type = right.Type ?? left.Type,
    };

    // Concatenates two lists, treating null as empty. The list merge_how
    // action selects append (default), prepend, replace, or no_replace.
    private static IReadOnlyList<T>? Concat<T>(
        IReadOnlyList<T>? left,
        IReadOnlyList<T>? right,
        CloudInitMergeOptions options)
    {
        switch (options.List)
        {
            // The incoming fragment's list wins outright; a fragment that does
            // not set the key (right is null) leaves the accumulated list.
            case ListMergeAction.Replace:
                return right ?? left;
            // Keep the accumulated list once it has entries.
            case ListMergeAction.NoReplace:
                return left is { Count: > 0 } ? left : right;
            case ListMergeAction.Prepend:
                return ConcatInOrder(right, left);
            default:
                return ConcatInOrder(left, right);
        }
    }

    private static IReadOnlyList<T>? ConcatInOrder<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second)
    {
        if (first is null || first.Count == 0) return second;
        if (second is null || second.Count == 0) return first;
        var combined = new List<T>(first.Count + second.Count);
        combined.AddRange(first);
        combined.AddRange(second);
        return combined;
    }

    // Merges two dictionaries: right entries replace same-key left entries,
    // left-only entries are preserved. Mirrors cloud-init's dict-merge
    // semantics for apt.sources, yum_repos, puppet.conf, etc. — later
    // declarations win on conflict, novel keys accumulate. dict(replace)
    // swaps the whole accumulated dictionary for the incoming one.
    private static IReadOnlyDictionary<TKey, TValue>? MergeDict<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? left,
        IReadOnlyDictionary<TKey, TValue>? right,
        CloudInitMergeOptions options) where TKey : notnull
    {
        if (options.Dict == DictMergeAction.Replace)
            return right ?? left;
        if (left is null || left.Count == 0) return right;
        if (right is null || right.Count == 0) return left;

        var merged = new Dictionary<TKey, TValue>(left.Count + right.Count);
        foreach (var kvp in left)
            merged[kvp.Key] = kvp.Value;
        foreach (var kvp in right)
            merged[kvp.Key] = kvp.Value;
        return merged;
    }

    // Dictionary merge variant where the value type itself carries deep-merge
    // semantics (e.g. AptSourceEntry, YumRepoConfig). For shared keys the
    // per-record merger runs; novel keys flow through untouched.
    private static IReadOnlyDictionary<TKey, TValue>? MergeDict<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? left,
        IReadOnlyDictionary<TKey, TValue>? right,
        Func<TValue?, TValue?, CloudInitMergeOptions, TValue?> merge,
        CloudInitMergeOptions options) where TKey : notnull
    {
        if (options.Dict == DictMergeAction.Replace)
            return right ?? left;
        if (left is null || left.Count == 0) return right;
        if (right is null || right.Count == 0) return left;

        var merged = new Dictionary<TKey, TValue>(left.Count + right.Count);
        foreach (var kvp in left)
            merged[kvp.Key] = kvp.Value;
        foreach (var kvp in right)
        {
            if (merged.TryGetValue(kvp.Key, out var existing))
            {
                var combined = merge(existing, kvp.Value, options);
                if (combined is not null)
                    merged[kvp.Key] = combined;
            }
            else
            {
                merged[kvp.Key] = kvp.Value;
            }
        }
        return merged;
    }

    // Merges two lists keyed by a name selector: entries with the same
    // (non-null) name are deep-merged (incoming wins on conflict) and unique
    // entries are kept. The list merge_how action selects ordering/priority,
    // mirroring Concat so users/groups honour the same directives as plain
    // lists:
    //   - replace    → the incoming list wins outright
    //   - no_replace → keep the accumulated list once it has entries
    //   - prepend    → incoming entries first, then novel accumulated entries
    //   - append     → accumulated entries first, then novel incoming (default)
    private static IReadOnlyList<T>? MergeByName<T>(
        IReadOnlyList<T>? left,
        IReadOnlyList<T>? right,
        Func<T, string?> nameSelector,
        Func<T, T, CloudInitMergeOptions, T> merge,
        CloudInitMergeOptions options) where T : class
    {
        switch (options.List)
        {
            case ListMergeAction.Replace:
                return right ?? left;
            case ListMergeAction.NoReplace:
                return left is { Count: > 0 } ? left : right;
        }

        if (left is null || left.Count == 0) return right;
        if (right is null || right.Count == 0) return left;

        // Prepend lays the incoming entries down first; append (default) lays
        // the accumulated entries first. Either way a same-named pair is
        // deep-merged with the incoming (right) entry winning, and it keeps the
        // slot of whichever list seeds the result.
        var (first, second) = options.List == ListMergeAction.Prepend
            ? (right, left)
            : (left, right);
        var rightIsSecond = options.List != ListMergeAction.Prepend;

        var result = new List<T>(left.Count + right.Count);
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in first)
        {
            var name = nameSelector(item);
            if (name is not null)
                indexByName[name] = result.Count;
            result.Add(item);
        }

        foreach (var item in second)
        {
            var name = nameSelector(item);
            if (name is not null && indexByName.TryGetValue(name, out var idx))
            {
                // Always merge as (accumulated-left, incoming-right) so the
                // incoming fragment wins regardless of which list seeded the slot.
                result[idx] = rightIsSecond
                    ? merge(result[idx], item, options)
                    : merge(item, result[idx], options);
            }
            else
            {
                if (name is not null)
                    indexByName[name] = result.Count;
                result.Add(item);
            }
        }

        return result;
    }
}
