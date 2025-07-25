namespace Eryph.GuestServices.Pty;

/// <summary>
/// The error codes which are used by the SSH PTY support.
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
///     <facility><c>0x003</c></facility>
///     <description>The PTY support</description>
///   </item>
/// </list>
/// </remarks>
public static class PtyErrorCodes
{
    public static readonly int GenericError = unchecked((int)0xa003_0001);

    public static readonly int FailedToParseArguments = unchecked((int)0xa003_0100);

    public static readonly int LinuxExitCode = unchecked((int)0xa003_9900);
}
