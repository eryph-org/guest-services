using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Coverage for the SSH foundation OS primitives (RFC 0018). The
/// <see cref="WindowsOs.MergeAuthorizedKeys"/> cases are pure (no filesystem)
/// and run everywhere; the sshd_config / RID-500 cases spawn / touch real OS
/// state and are gated by <see cref="OperatingSystem.IsWindows"/> per repo
/// convention.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsSshTests
{
    // ---- OpenSSH Server capability availability (by OS build) ----

    [Theory]
    [InlineData(10, 0, 14393, false)] // Windows Server 2016 — no capability
    [InlineData(10, 0, 17134, false)] // Windows 10 1803 — server capability not yet supported
    [InlineData(10, 0, 17763, true)]  // Windows Server 2019 / Win10 1809 — first supported
    [InlineData(10, 0, 20348, true)]  // Windows Server 2022
    [InlineData(10, 0, 26100, true)]  // Windows Server 2025
    [InlineData(6, 3, 9600, false)]   // Windows Server 2012 R2
    public void OpenSshServerCapability_availability_follows_the_1809_build_floor(
        int major, int minor, int build, bool expected)
    {
        WindowsOs.IsOpenSshServerCapabilityAvailable(new Version(major, minor, build))
            .Should().Be(expected);
    }

    // ---- MERGE + dedup (Finding 6 regression) ----

    [Fact]
    public void MergeAuthorizedKeys_UnionsExistingAndNew_Finding6()
    {
        var merged = WindowsOs.MergeAuthorizedKeys(
            existingLines: ["ssh-ed25519 AAA keyA"],
            newKeys: ["ssh-ed25519 BBB keyB"]);

        merged.Should().HaveCount(2);
        merged[0].Should().Be("ssh-ed25519 AAA keyA");
        merged[1].Should().Be("ssh-ed25519 BBB keyB");
    }

    [Fact]
    public void MergeAuthorizedKeys_DeduplicatesIdenticalKey_Finding6()
    {
        var merged = WindowsOs.MergeAuthorizedKeys(
            existingLines: ["ssh-ed25519 AAA keyA"],
            newKeys: ["ssh-ed25519 AAA keyA"]);

        merged.Should().ContainSingle().Which.Should().Be("ssh-ed25519 AAA keyA");
    }

    [Fact]
    public void MergeAuthorizedKeys_DeduplicatesSameBodyDifferentComment_Finding6()
    {
        // Same type + base64 body, different trailing comment → one key, the
        // existing line's form preserved.
        var merged = WindowsOs.MergeAuthorizedKeys(
            existingLines: ["ssh-ed25519 AAA alice@old"],
            newKeys: ["ssh-ed25519 AAA alice@new"]);

        merged.Should().ContainSingle().Which.Should().Be("ssh-ed25519 AAA alice@old");
    }

    [Fact]
    public void MergeAuthorizedKeys_PreservesExistingOrderAndAppendsNew()
    {
        var merged = WindowsOs.MergeAuthorizedKeys(
            existingLines: ["ssh-rsa R1 first", "ssh-ed25519 E1 second"],
            newKeys: ["ssh-ed25519 E1 second-dup", "ssh-ecdsa C1 third"]);

        merged.Should().Equal(
            "ssh-rsa R1 first",
            "ssh-ed25519 E1 second",
            "ssh-ecdsa C1 third");
    }

    [Fact]
    public void MergeAuthorizedKeys_EmptyInputAndNoExisting_YieldsEmpty()
    {
        WindowsOs.MergeAuthorizedKeys(existingLines: [], newKeys: []).Should().BeEmpty();
    }

    [Fact]
    public void MergeAuthorizedKeys_KeysWithOptionsPrefix_DedupOnBody()
    {
        // A leading options field must not break body extraction.
        var merged = WindowsOs.MergeAuthorizedKeys(
            existingLines: ["no-pty ssh-ed25519 AAA alice"],
            newKeys: ["ssh-ed25519 AAA alice"]);

        merged.Should().ContainSingle().Which.Should().Be("no-pty ssh-ed25519 AAA alice");
    }

    // ---- sshd_config Include (pure) ----

    [Fact]
    public void BuildSshdManagedConfig_PrependsManagedBlockAboveShippedConfig()
    {
        var result = WindowsOs.BuildSshdManagedConfig(
            "# shipped config\nPasswordAuthentication yes\n",
            "PubkeyAuthentication yes\n");

        result.Should().NotBeNull();
        // Directives are INLINED (not an `Include`), so sshd < 8.2 can read them.
        result!.Should().NotContain("Include");
        result.Split('\n')[0].Should().Be(WindowsOs.SshdManagedBeginMarker);
        result.Should().Contain(WindowsOs.SshdManagedBeginMarker);
        result.Should().Contain(WindowsOs.SshdManagedEndMarker);
        // Our block is above the shipped content so first-obtained-value wins.
        result.IndexOf("PubkeyAuthentication yes", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("# shipped config", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSshdManagedConfig_IsIdempotent()
    {
        var first = WindowsOs.BuildSshdManagedConfig(
            "# shipped\nPasswordAuthentication yes\n", "PubkeyAuthentication yes\n");
        first.Should().NotBeNull();

        // Second pass over our own output with the same directives = no-op.
        var second = WindowsOs.BuildSshdManagedConfig(first!, "PubkeyAuthentication yes\n");
        second.Should().BeNull("the managed block must be written exactly once");
    }

    [Fact]
    public void BuildSshdManagedConfig_ReplacesPriorBlockAndChangedDirectives()
    {
        var first = WindowsOs.BuildSshdManagedConfig(
            "# shipped\n", "PubkeyAuthentication yes\n");

        // A changed directive set replaces the old block, not appends a second.
        var second = WindowsOs.BuildSshdManagedConfig(
            first!, "PubkeyAuthentication yes\nPasswordAuthentication no\n");

        second.Should().NotBeNull();
        second!.Split('\n').Count(l => l == WindowsOs.SshdManagedBeginMarker)
            .Should().Be(1, "the old managed block must be replaced, not duplicated");
        second.Should().Contain("PasswordAuthentication no");
        second.Should().Contain("# shipped");
    }

    [Fact]
    public void BuildSshdManagedConfig_StripsLegacyIncludeFromOldGuests()
    {
        // A guest provisioned by the old (Include drop-in) release: the stale
        // Include line must be removed, since sshd < 8.2 rejects it.
        var result = WindowsOs.BuildSshdManagedConfig(
            "Include sshd_config.d/*.conf\n# shipped\n", "PubkeyAuthentication yes\n");

        result.Should().NotBeNull();
        result!.Should().NotContain("Include sshd_config.d/*.conf");
        result.Should().Contain("# shipped");
    }

    [Fact]
    public void BuildSshdManagedConfig_EmptyFileGetsJustTheBlock()
    {
        var result = WindowsOs.BuildSshdManagedConfig("", "PubkeyAuthentication yes\n");

        result.Should().Be(
            WindowsOs.SshdManagedBeginMarker + "\n"
            + "PubkeyAuthentication yes\n"
            + WindowsOs.SshdManagedEndMarker + "\n");
    }

    [Fact]
    public void BuildSshdManagedConfig_UnterminatedBeginMarkerDoesNotEatTheRestOfTheFile()
    {
        // A hand-edited/truncated file with a BEGIN but no END must NOT have the
        // vendor content (e.g. the trailing `Match Group administrators` block)
        // silently discarded.
        var existing = WindowsOs.SshdManagedBeginMarker + "\n"
            + "PubkeyAuthentication yes\n"
            + "Match Group administrators\n"
            + "  AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys\n";

        var result = WindowsOs.BuildSshdManagedConfig(existing, "PubkeyAuthentication yes\n");

        result.Should().NotBeNull();
        result!.Should().Contain("Match Group administrators");
        result.Should().Contain("administrators_authorized_keys");
        // Exactly one (fresh, terminated) managed block ends up present.
        result.Split('\n').Count(l => l == WindowsOs.SshdManagedBeginMarker).Should().Be(1);
        result.Split('\n').Count(l => l == WindowsOs.SshdManagedEndMarker).Should().Be(1);
    }

    [Fact]
    public void BuildSshdManagedConfig_NormalizesCrlfInput()
    {
        var result = WindowsOs.BuildSshdManagedConfig(
            "# shipped\r\nPasswordAuthentication yes\r\n", "PubkeyAuthentication yes\n");

        result.Should().NotBeNull();
        result!.Should().NotContain("\r");
        result.Should().Contain("# shipped");
    }

    // ---- RID-500 (Windows-only) ----

    [SupportedOSPlatform("windows")]
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResolveBuiltinAdministratorNameAsync_ReturnsNonEmptyName()
    {
        if (!OperatingSystem.IsWindows()) return;

        var os = new WindowsOs(NullLogger<WindowsOs>.Instance);
        var name = await os.ResolveBuiltinAdministratorNameAsync(CancellationToken.None);

        name.Should().NotBeNullOrWhiteSpace();
    }
}
