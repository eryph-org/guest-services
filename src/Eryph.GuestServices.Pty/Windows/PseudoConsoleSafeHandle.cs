using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Eryph.GuestServices.Pty.Windows;

internal partial class PseudoConsoleSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        ClosePseudoConsole(handle);
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void ClosePseudoConsole(nint handle);
}
