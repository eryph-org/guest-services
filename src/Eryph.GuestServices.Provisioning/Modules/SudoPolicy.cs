namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Shared Windows-side interpretation of cloud-init's <c>sudo</c> field.
/// Extracted from <see cref="UsersGroupsModule"/> so the
/// <see cref="IDefaultUserResolver"/> and the SshModule answer the
/// "is this user an administrator" question identically.
/// </summary>
internal static class SudoPolicy
{
    /// <summary>
    /// Cloud-init's <c>sudo</c> is a string-or-list union; the schema carries
    /// it as <c>IReadOnlyList&lt;string&gt;?</c>. This Windows-side shim
    /// collapses the list to the binary "is this user an Administrator"
    /// answer because there is no Windows equivalent of per-rule sudoers
    /// semantics (NOPASSWD, runas restrictions, command lists).
    /// <para>
    /// Decision rule (locked by tests): the user is promoted to
    /// Administrators if at least one non-empty entry exists that is not the
    /// literal string <c>"false"</c> (case-insensitive, trimmed). An entry of
    /// <c>"false"</c> mixed with other entries does NOT veto promotion — any
    /// non-false entry wins. Empty / null / list-of-only-"false" → no
    /// promotion. Per-rule sudoers semantics are platform-irrelevant on
    /// Windows and intentionally not modeled.
    /// </para>
    /// </summary>
    public static bool IsSudoEnabled(IReadOnlyList<string>? sudo)
    {
        if (sudo is null || sudo.Count == 0)
            return false;

        foreach (var entry in sudo)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var trimmed = entry.Trim();
            // cloud-init treats anything other than "false" (case-insensitive) as
            // "this user gets sudo". On Windows that means Administrators.
            if (!string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
