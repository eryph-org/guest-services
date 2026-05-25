using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// P/Invoke surface for <c>advapi32.dll</c>. We only need
/// <see cref="LookupAccountSid"/> to translate well-known SIDs to the
/// localized account name, e.g. the built-in Administrators group.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Advapi32
{
    public enum SID_NAME_USE
    {
        SidTypeUser = 1,
        SidTypeGroup,
        SidTypeDomain,
        SidTypeAlias,
        SidTypeWellKnownGroup,
        SidTypeDeletedAccount,
        SidTypeInvalid,
        SidTypeUnknown,
        SidTypeComputer,
        SidTypeLabel,
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LookupAccountSid(
        string? lpSystemName,
        byte[] Sid,
        System.Text.StringBuilder? lpName,
        ref uint cchName,
        System.Text.StringBuilder? ReferencedDomainName,
        ref uint cchReferencedDomainName,
        out SID_NAME_USE peUse);
}
