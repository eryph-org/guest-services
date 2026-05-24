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
// The per-property merge code is source-generated from the model's
// [CloudInitRoot] / [CloudInitRecord] / [CloudInitField] / [MergeBehavior]
// attributes — see Eryph.GuestServices.CloudConfig.SourceGen. The hand-
// written helpers below stay here so generated code can call them by name:
// they handle the cloud-init specific merge semantics (deep-merge users
// keyed by Name, concat lists, etc.) that are not pure per-property
// scalar overrides.
public static partial class CloudConfigMerge
{
    private static UserConfig MergeUser(UserConfig left, UserConfig right) => new()
    {
        Name = right.Name ?? left.Name,
        Passwd = right.Passwd ?? left.Passwd,
        PlainTextPasswd = right.PlainTextPasswd ?? left.PlainTextPasswd,
        HashedPasswd = right.HashedPasswd ?? left.HashedPasswd,
        LockPasswd = right.LockPasswd ?? left.LockPasswd,
        Groups = Concat(left.Groups, right.Groups),
        SshAuthorizedKeys = Concat(left.SshAuthorizedKeys, right.SshAuthorizedKeys),
        Inactive = right.Inactive ?? left.Inactive,
        Shell = right.Shell ?? left.Shell,
        HomeDir = right.HomeDir ?? left.HomeDir,
        PrimaryGroup = right.PrimaryGroup ?? left.PrimaryGroup,
        // Sudo widened from string? to IReadOnlyList<string>? — cloud-init
        // accepts a single string OR a list of strings, and stacking two
        // fragments that each carry sudoers lines concatenates them.
        Sudo = Concat(left.Sudo, right.Sudo),
        System = right.System ?? left.System,
        Gecos = right.Gecos ?? left.Gecos,
        SshImportId = Concat(left.SshImportId, right.SshImportId),
        SshRedirectUser = right.SshRedirectUser ?? left.SshRedirectUser,
        Expiredate = right.Expiredate ?? left.Expiredate,
        NoCreateHome = right.NoCreateHome ?? left.NoCreateHome,
        NoUserGroup = right.NoUserGroup ?? left.NoUserGroup,
        NoLogInit = right.NoLogInit ?? left.NoLogInit,
        SelinuxUser = right.SelinuxUser ?? left.SelinuxUser,
        Uid = right.Uid ?? left.Uid,
        Snapuser = right.Snapuser ?? left.Snapuser,
    };

    private static GroupConfig MergeGroup(GroupConfig left, GroupConfig right) => new()
    {
        Name = right.Name ?? left.Name,
        Members = Concat(left.Members, right.Members),
        Gid = right.Gid ?? left.Gid,
    };

    private static ChpasswdListEntry MergeChpasswdEntry(ChpasswdListEntry left, ChpasswdListEntry right) => new()
    {
        Name = right.Name ?? left.Name,
        Password = right.Password ?? left.Password,
        Type = right.Type ?? left.Type,
    };

    // Concatenates two lists, treating null as empty.
    private static IReadOnlyList<T>? Concat<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (left is null || left.Count == 0) return right;
        if (right is null || right.Count == 0) return left;
        var combined = new List<T>(left.Count + right.Count);
        combined.AddRange(left);
        combined.AddRange(right);
        return combined;
    }

    // Merges two lists keyed by a name selector: entries with the same
    // (non-null) name are replaced by the later one (deep-merged), and
    // unique entries are kept in left-then-right order.
    private static IReadOnlyList<T>? MergeByName<T>(
        IReadOnlyList<T>? left,
        IReadOnlyList<T>? right,
        Func<T, string?> nameSelector,
        Func<T, T, T> merge) where T : class
    {
        if (left is null || left.Count == 0) return right;
        if (right is null || right.Count == 0) return left;

        var result = new List<T>(left.Count + right.Count);
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in left)
        {
            var name = nameSelector(item);
            if (name is not null)
                indexByName[name] = result.Count;
            result.Add(item);
        }

        foreach (var item in right)
        {
            var name = nameSelector(item);
            if (name is not null && indexByName.TryGetValue(name, out var idx))
            {
                result[idx] = merge(result[idx], item);
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
