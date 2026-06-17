using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Update;

namespace Eryph.GuestServices.Provisioning.Tests.Update;

public sealed class ReleaseSignatureVerifierTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "update", name));

    private static readonly byte[] Sums = Fixture("eryph_guest-services_0.4.0_SHA256SUMS");
    private static readonly byte[] Sig = Fixture("eryph_guest-services_0.4.0_SHA256SUMS.sig");

    [Fact]
    public void Verifies_the_real_dbosoft_release_signature()
    {
        // The bundled keyring must verify the actual detached signature over the
        // actual 0.4.0 SHA256SUMS — proving the embedded keys are the ones that
        // signed the real release.
        new ReleaseSignatureVerifier().Verify(Sums, Sig).Should().BeTrue();
    }

    [Fact]
    public void Rejects_tampered_checksums()
    {
        // Flip a byte in the signed data: the signature must no longer verify.
        var tampered = (byte[])Sums.Clone();
        tampered[0] ^= 0xFF;
        new ReleaseSignatureVerifier().Verify(tampered, Sig).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_garbage_signature()
    {
        new ReleaseSignatureVerifier().Verify(Sums, [0x01, 0x02, 0x03]).Should().BeFalse();
    }

    [Theory]
    [InlineData(new byte[0])]                                  // empty
    [InlineData(new byte[] { 0x99, 0x00 })]                    // truncated packet header
    [InlineData(new byte[] { 0x2D, 0x2D, 0x2D, 0x2D, 0x2D })]  // looks like an armor "-----" start
    public void Malformed_signature_returns_false_without_throwing(byte[] signature)
    {
        // The signature bytes are attacker-controlled; parsing must never throw.
        var verifier = new ReleaseSignatureVerifier();
        var act = () => verifier.Verify(Sums, signature);
        act.Should().NotThrow();
        verifier.Verify(Sums, signature).Should().BeFalse();
    }

    [Fact]
    public void Signed_sums_lists_the_windows_service_package_hash()
    {
        // The signed SUMS is the authority for the package hash the updater
        // checks the download against.
        var sums = Sha256Sums.Parse(System.Text.Encoding.UTF8.GetString(Sums));
        sums.GetHash("egs_0.4.0_windows_amd64.zip")
            .Should().Be("ef0c7fbb4396696c9401b86952382f88937836d89fdbfc545888d2fced752367");
    }
}
