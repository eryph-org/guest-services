using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Eryph.GuestServices.Pty.WindowsLegacy;

namespace Eryph.GuestServices.Pty;

public static class PtyProvider
{
    public static IPty CreatePty()
    {
        if (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 17763)
            return new Windows.WindowsPty();

        if (OperatingSystem.IsWindows())
            return new WindowsLegacyPty();

        if (OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux support is not implemented yet.");

        throw new PlatformNotSupportedException(
            "Pty support is only available on Windows and Linux platforms.");
    }
}
