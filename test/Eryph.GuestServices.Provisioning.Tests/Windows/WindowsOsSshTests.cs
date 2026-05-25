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
    public void EnsureSshdConfigInclude_PrependsWhenMissing()
    {
        var result = WindowsOs.EnsureSshdConfigInclude("# shipped config\nPasswordAuthentication yes\n");

        result.Should().NotBeNull();
        result!.Split('\n')[0].Should().Be("Include sshd_config.d/*.conf");
        result.Should().Contain("PasswordAuthentication yes");
    }

    [Fact]
    public void EnsureSshdConfigInclude_IsIdempotentWhenAlreadyPresent()
    {
        // Second pass over already-included content must be a no-op (null).
        var first = WindowsOs.EnsureSshdConfigInclude("PasswordAuthentication yes\n");
        first.Should().NotBeNull();

        var second = WindowsOs.EnsureSshdConfigInclude(first!);
        second.Should().BeNull("the Include directive must be added exactly once");
    }

    [Fact]
    public void EnsureSshdConfigInclude_IgnoresCommentedInclude()
    {
        // A commented-out Include does not count — the real directive is added.
        var result = WindowsOs.EnsureSshdConfigInclude("# Include sshd_config.d/*.conf\n");

        result.Should().NotBeNull();
        result!.Split('\n')[0].Should().Be("Include sshd_config.d/*.conf");
    }

    [Fact]
    public void EnsureSshdConfigInclude_EmptyFileGetsInclude()
    {
        var result = WindowsOs.EnsureSshdConfigInclude("");

        result.Should().Be("Include sshd_config.d/*.conf\n");
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
