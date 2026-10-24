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

    [Fact]
    public void A_create_grantable_system_directory_is_trusted_to_execute_but_not_to_discover()
    {
        if (!OperatingSystem.IsWindows()) return;

        // %ProgramData% is SYSTEM-owned (safe to execute from — nobody can replace
        // our files), yet grants BUILTIN\Users create rights by default. A planted
        // *.dsc.resource.json there would run as LocalSystem, so it must NOT be
        // accepted as a DSC discovery directory.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        WindowsOs.IsTrustedSystemPath(programData).Should().BeTrue();
        WindowsOs.IsTrustedDiscoveryDirectory(programData).Should().BeFalse();
    }

    [Fact]
    public void A_system_directory_is_trusted_to_discover()
    {
        if (!OperatingSystem.IsWindows()) return;

        // System32 (TrustedInstaller-owned, no non-privileged create rights) is a
        // legitimate discovery directory and must still pass.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        WindowsOs.IsTrustedDiscoveryDirectory(system32).Should().BeTrue();
    }

    [Fact]
    public void A_unc_path_is_not_a_trusted_discovery_directory()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A remote/UNC directory's ACL says nothing about what the other end can
        // change before dsc reads from it, so it is never a discovery directory.
        WindowsOs.IsTrustedDiscoveryDirectory(@"\\server\share\bin").Should().BeFalse();
    }

    [Fact]
    public void FilterTrustedPathEntries_drops_a_create_grantable_directory()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The discovery PATH must exclude a directory where a standard user can
        // drop a manifest, even when it is safe to execute from.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var entries = WindowsOs.FilterTrustedPathEntries($"{system32};{programData}")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        entries.Should().Contain(system32);
        entries.Should().NotContain(programData);
    }
}
