using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// Resolves the localized name of well-known Windows groups via their SIDs.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WellKnownGroups
{
    private const string AdministratorsFallback = "Administrators";

    private static string? _administratorsCache;
    private static int _fallbackWarned;

    // The logger is injected by WindowsOs via SetLogger so this static helper can
    // emit a one-shot warning when it falls back to the English literal.
    private static ILogger _logger = NullLogger.Instance;

    public static void SetLogger(ILogger logger) => _logger = logger;

    public static string AdministratorsName()
    {
        if (_administratorsCache is not null)
            return _administratorsCache;

        try
        {
            var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);

            uint nameLen = 256;
            uint domainLen = 256;
            var name = new StringBuilder((int)nameLen);
            var domain = new StringBuilder((int)domainLen);

            if (Advapi32.LookupAccountSid(null, bytes, name, ref nameLen, domain, ref domainLen, out _))
            {
                _administratorsCache = name.ToString();
                return _administratorsCache;
            }

            var lastError = Marshal.GetLastWin32Error();
            WarnOnceFallback(lastError, exception: null);
        }
        catch (Exception ex)
        {
            WarnOnceFallback(lastError: 0, ex);
        }

        // SID-based lookup is purely for localization. The English literal works
        // on every English-locale Windows install, which is the documented case.
        _administratorsCache = AdministratorsFallback;
        return _administratorsCache;
    }

    private static void WarnOnceFallback(int lastError, Exception? exception)
    {
        if (Interlocked.Exchange(ref _fallbackWarned, 1) != 0)
            return;

        if (exception is not null)
            _logger.LogWarning(exception,
                "Failed to resolve localized Administrators group name via LookupAccountSid; falling back to '{Fallback}'.",
                AdministratorsFallback);
        else
            _logger.LogWarning(
                "LookupAccountSid for BuiltinAdministratorsSid failed with Win32 error {LastError}; falling back to '{Fallback}'.",
                lastError, AdministratorsFallback);
    }
}
