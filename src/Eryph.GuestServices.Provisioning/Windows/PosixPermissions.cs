using System.Security.AccessControl;

namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Translates POSIX file-permission octal triplets into Windows NTFS access
/// rights. The cloud-config <c>write_files.permissions</c> field carries a
/// Unix octal like <c>0644</c> that's meaningful on Linux but doesn't map
/// natively to NTFS ACLs. Cloudbase-init applies a similar wrapper; we mirror
/// the behavior so cloud-config authored for Linux behaves sensibly on Windows.
/// </summary>
/// <remarks>
/// Mapping per POSIX triplet bit:
/// <list type="bullet">
///   <item><term>4 — read</term><description>FileSystemRights.Read + ReadPermissions + ReadAttributes</description></item>
///   <item><term>2 — write</term><description>FileSystemRights.Write + ReadAndExecute-write peers</description></item>
///   <item><term>1 — execute</term><description>FileSystemRights.ExecuteFile + ReadAndExecute</description></item>
/// </list>
/// Windows ACLs are richer than POSIX; this translation is intentionally lossy.
/// Existing inherited ACEs (SYSTEM, Administrators) are preserved by the
/// caller — this class only computes the new ACEs to ADD for the owner,
/// primary group, and "Everyone".
/// </remarks>
public static class PosixPermissions
{
    /// <summary>Translates a POSIX triplet digit (0–7) to the NTFS rights equivalent.</summary>
    public static FileSystemRights TripletToRights(int triplet)
    {
        if (triplet is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(triplet),
                triplet, "POSIX permission triplet must be in [0, 7].");

        FileSystemRights rights = 0;

        if ((triplet & 4) != 0)
            rights |= FileSystemRights.Read | FileSystemRights.ReadPermissions | FileSystemRights.ReadAttributes;

        if ((triplet & 2) != 0)
            rights |= FileSystemRights.Write | FileSystemRights.WriteAttributes
                      | FileSystemRights.AppendData | FileSystemRights.WriteData;

        if ((triplet & 1) != 0)
            rights |= FileSystemRights.ExecuteFile | FileSystemRights.ReadAndExecute;

        return rights;
    }

    /// <summary>
    /// Parses a POSIX octal permission string (e.g. <c>"0644"</c>, <c>"644"</c>,
    /// <c>"0o644"</c>) into the three triplets (owner, group, others).
    /// </summary>
    public static (int Owner, int Group, int Others) Parse(string permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissions);
        var digits = permissions.Trim();
        // Reject hex prefix outright (matches the YAML converter): 0x644 is hex
        // syntax, not octal, even if all chars happen to fall in [0-7].
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"'{permissions}' uses a hex prefix; POSIX permissions must be octal.",
                nameof(permissions));

        // Python-style 0o prefix is accepted as a normalisation pass.
        if (digits.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            digits = digits[2..];

        foreach (var c in digits)
        {
            if (c < '0' || c > '7')
                throw new ArgumentException(
                    $"'{permissions}' is not a valid POSIX octal permission.",
                    nameof(permissions));
        }

        if (digits.Length < 3 || digits.Length > 4)
            throw new ArgumentException(
                $"'{permissions}' must have 3 or 4 octal digits (got {digits.Length}).",
                nameof(permissions));

        // 4-digit form leads with the setuid/setgid/sticky bit (currently ignored).
        var tail = digits.Length == 4 ? digits[1..] : digits;
        return (tail[0] - '0', tail[1] - '0', tail[2] - '0');
    }
}
