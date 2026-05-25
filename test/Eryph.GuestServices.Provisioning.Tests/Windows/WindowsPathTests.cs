using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Pure unit tests for <see cref="WindowsPath"/>. These exercise Windows path
/// semantics (drive-letter rooted, <c>\</c>-separated) directly, with no
/// dependency on the host OS — so they are deterministic and pass identically
/// on Windows and Linux. They guard the determinism fix that removed
/// <c>System.IO.Path</c> (host-separator) usage from the modules and
/// <see cref="WindowsOs.TranslateUnixPath"/>.
/// </summary>
public sealed class WindowsPathTests
{
    [Theory]
    [InlineData(@"C:\etc\foo", @"C:\etc")]
    [InlineData(@"C:\etc\cloud\cloud.cfg", @"C:\etc\cloud")]
    [InlineData(@"C:\foo", @"C:\")]
    [InlineData(@"C:\Users\alice\.ssh\authorized_keys", @"C:\Users\alice\.ssh")]
    // Forward slashes on input are tolerated and normalized.
    [InlineData(@"C:/etc/foo", @"C:\etc")]
    // Trailing separator is ignored.
    [InlineData(@"C:\etc\foo\", @"C:\etc")]
    public void GetDirectoryName_returns_parent(string input, string expected)
    {
        WindowsPath.GetDirectoryName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("")]
    [InlineData(null)]
    public void GetDirectoryName_returns_null_when_no_parent(string? input)
    {
        WindowsPath.GetDirectoryName(input).Should().BeNull();
    }

    [Theory]
    [InlineData(new[] { @"C:\temp\eryph-scripts-test", "001-do-thing.ps1" }, @"C:\temp\eryph-scripts-test\001-do-thing.ps1")]
    [InlineData(new[] { @"C:\ProgramData\eryph\provisioning\logs", "001-hello.ps1.log" }, @"C:\ProgramData\eryph\provisioning\logs\001-hello.ps1.log")]
    [InlineData(new[] { @"C:\Users", "alice", ".ssh", "authorized_keys" }, @"C:\Users\alice\.ssh\authorized_keys")]
    // A trailing separator on the head must not produce a doubled separator.
    [InlineData(new[] { @"C:\dir\", "file.txt" }, @"C:\dir\file.txt")]
    public void Combine_joins_with_backslash(string[] parts, string expected)
    {
        WindowsPath.Combine(parts).Should().Be(expected);
    }

    [Fact]
    public void Combine_skips_empty_parts()
    {
        WindowsPath.Combine(@"C:\dir", "", "file.txt").Should().Be(@"C:\dir\file.txt");
    }

    [Fact]
    public void Combine_rooted_part_resets_the_accumulator()
    {
        WindowsPath.Combine(@"C:\dir", @"D:\other").Should().Be(@"D:\other");
    }

    [Theory]
    [InlineData(@"C:\etc\foo", @"C:\etc\foo")]
    [InlineData(@"C:\Users\alice", @"C:\Users\alice")]
    // Forward slashes normalized.
    [InlineData(@"C:/forward/slashes", @"C:\forward\slashes")]
    // Redundant separators and single-dot segments collapse.
    [InlineData(@"C:\etc\.\foo", @"C:\etc\foo")]
    [InlineData(@"C:\etc\\foo", @"C:\etc\foo")]
    public void GetFullPath_canonicalizes_drive_rooted_paths(string input, string expected)
    {
        WindowsPath.GetFullPath(input).Should().Be(expected);
    }

    [Fact]
    public void GetFullPath_keeps_bare_drive_root()
    {
        WindowsPath.GetFullPath(@"C:\").Should().Be(@"C:\");
    }

    [Theory]
    [InlineData(@"\\server\share\foo", @"\\server\share\foo")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\a\.\b", @"\\server\share\a\b")]
    public void GetFullPath_handles_unc_roots(string input, string expected)
    {
        WindowsPath.GetFullPath(input).Should().Be(expected);
    }

    [Fact]
    public void GetFullPath_resolves_dotdot_without_escaping_root()
    {
        // The TranslateUnixPath guard rejects ".." before reaching GetFullPath;
        // this proves the canonicalizer itself never climbs above the root even
        // if a ".." slips through.
        WindowsPath.GetFullPath(@"C:\etc\sub\..\foo").Should().Be(@"C:\etc\foo");
        WindowsPath.GetFullPath(@"C:\..\..\foo").Should().Be(@"C:\foo");
    }

    [Theory]
    [InlineData(@"C:\foo")]
    [InlineData(@"c:/foo")]
    [InlineData(@"\\server\share")]
    [InlineData(@"/unix/style")]
    public void IsRooted_true_for_rooted_paths(string input)
    {
        WindowsPath.IsRooted(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("relative\\path")]
    [InlineData("")]
    public void IsRooted_false_for_relative_paths(string input)
    {
        WindowsPath.IsRooted(input).Should().BeFalse();
    }
}
