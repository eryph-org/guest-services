using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Coverage for <see cref="WindowsOs.IsTrustedSystemPath"/>, the trusted-path
/// gate that refuses to execute a winget wrapper / choco.exe a standard user
/// could have planted (LocalSystem privilege escalation). Touches real
/// filesystem ACLs, so gated by <see cref="OperatingSystem.IsWindows"/> per
/// repo convention.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsTrustedPathTests
{
    [Fact]
    public void A_system_directory_is_trusted()
    {
        if (!OperatingSystem.IsWindows()) return;

        // System32: owned by TrustedInstaller, no non-privileged write, and its
        // ancestor (%windir%) is a trusted anchor.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        WindowsOs.IsTrustedSystemPath(system32).Should().BeTrue();
    }

    [Fact]
    public void A_user_writable_directory_is_not_trusted()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A freshly created temp subdirectory inherits write access for the
        // current (non-privileged) user, so it must be rejected.
        var dir = Path.Combine(Path.GetTempPath(), "egs-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WindowsOs.IsTrustedSystemPath(dir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_path_under_a_trusted_root_is_trusted()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A not-yet-created leaf under %ProgramData% (e.g. the wrapper dir before
        // we create it) has no untrusted ancestor, so the chain is trusted.
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph-nonexistent-" + Guid.NewGuid().ToString("N"), "winget");
        WindowsOs.IsTrustedSystemPath(path).Should().BeTrue();
    }

    [Fact]
    public void FilterTrustedPathEntries_keeps_trusted_dirs_and_drops_the_rest()
    {
        if (!OperatingSystem.IsWindows()) return;

        // DSC discovers resource manifests by scanning PATH, so the PATH we hand
        // it must contain only trusted directories: System32 stays, a
        // user-writable temp directory and a non-existent entry are dropped.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var userDir = Path.Combine(Path.GetTempPath(), "egs-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        var missing = @"C:\egs-nonexistent-" + Guid.NewGuid().ToString("N");
        try
        {
            var entries = WindowsOs.FilterTrustedPathEntries($"{system32};{userDir};{missing}")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);

            entries.Should().Contain(system32);
            entries.Should().NotContain(userDir);
            entries.Should().NotContain(missing);
        }
        finally
        {
            Directory.Delete(userDir, recursive: true);
        }
    }
}
