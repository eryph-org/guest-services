using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// Resolves the localized name of well-known Windows groups via their SIDs.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WellKnownGroups
{
    private static string? _administratorsCache;

    public static string AdministratorsName()
    {
        if (_administratorsCache is not null)
            return _administratorsCache;

        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);

        uint nameLen = 256;
        uint domainLen = 256;
        var name = new StringBuilder((int)nameLen);
        var domain = new StringBuilder((int)domainLen);

        if (!Advapi32.LookupAccountSid(null, bytes, name, ref nameLen, domain, ref domainLen, out _))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "LookupAccountSid for BuiltinAdministratorsSid failed.");

        _administratorsCache = name.ToString();
        return _administratorsCache;
    }
}
