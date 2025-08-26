namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

/// <summary>
/// The error codes which are used by the SSH extensions.
/// </summary>
/// <remarks>
/// As the client is used in a Windows environment, we
/// return HRESULT-compatible error codes with custom
/// facility codes. The following facility codes are used:
/// <list type="table">
///   <listheader>
///     <facility>Facility</facility>
///     <description>Description</description>
///   </listheader>
///   <item>
///     <facility><c>0x002</c></facility>
///     <description>The custom file transfers</description>
///   </item>
/// </list>
/// </remarks>
public class ErrorCodes
{
    public static readonly int FileExists = unchecked((int)0xa002_0001);
    public static readonly int FileNotFound = unchecked((int)0xa002_0002);
}
