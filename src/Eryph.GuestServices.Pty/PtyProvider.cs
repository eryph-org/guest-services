using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Eryph.GuestServices.Pty.WindowsFallback;

namespace Eryph.GuestServices.Pty;

public static class PtyProvider
{
    public static IPty CreatePty()
    {
        if (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 17763)
            return new Windows.WindowsPty();

        if (OperatingSystem.IsWindows())
            return new WindowsFallbackPty();

        if (OperatingSystem.IsLinux())
            return new Linux.LinuxPty();

        throw new PlatformNotSupportedException(
            "Pty support is only available on Windows and Linux platforms.");
    }

}
