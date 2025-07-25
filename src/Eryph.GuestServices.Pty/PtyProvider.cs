using Eryph.GuestServices.Pty.Linux;
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
            return new LinuxPty();

        throw new PlatformNotSupportedException(
            "Pty support is only available on Windows and Linux platforms.");
    }
}
