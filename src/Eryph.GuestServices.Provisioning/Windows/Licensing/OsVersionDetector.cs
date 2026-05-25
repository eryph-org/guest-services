using System.Runtime.Versioning;

namespace Eryph.GuestServices.Provisioning.Windows.Licensing;

/// <summary>
/// Detects the Windows Server family by mapping
/// <see cref="Environment.OSVersion"/> build numbers to an
/// <see cref="OsVersionFamily"/>. .NET 5+ no longer clamps the manifest
/// version so the real build number is available here without any P/Invoke.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class OsVersionDetector
{
    public static OsVersionFamily Detect() => Detect(Environment.OSVersion.Version);

    // Test seam: passing an explicit Version keeps the resolver pure.
    public static OsVersionFamily Detect(Version osVersion)
    {
        if (osVersion.Major == 6 && osVersion.Minor == 2)
            return OsVersionFamily.WindowsServer2012;
        if (osVersion.Major == 6 && osVersion.Minor == 3)
            return OsVersionFamily.WindowsServer2012R2;
        if (osVersion.Major == 10 && osVersion.Minor == 0)
        {
            // Build-number transitions per Microsoft release docs. Server
            // 2019 and later all report (10, 0) — only the build changes.
            return osVersion.Build switch
            {
                >= 26100 => OsVersionFamily.WindowsServer2025,
                >= 20348 => OsVersionFamily.WindowsServer2022,
                >= 17763 => OsVersionFamily.WindowsServer2019,
                _ => OsVersionFamily.WindowsServer2016,
            };
        }
        return OsVersionFamily.Unknown;
    }
}
