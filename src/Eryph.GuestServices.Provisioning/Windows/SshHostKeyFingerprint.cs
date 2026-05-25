namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// A generated / written ssh host key surfaced for operator reporting.
/// Mirrors the shape <c>ssh-keygen -l</c> reports (the
/// <c>&lt;bits&gt; SHA256:... &lt;comment&gt; (&lt;type&gt;)</c> line).
/// </summary>
/// <param name="KeyType">The host-key type — <c>ed25519</c>, <c>ecdsa</c>, <c>rsa</c>.</param>
/// <param name="Fingerprint">The fingerprint in the <c>SHA256:...</c> form.</param>
/// <param name="PublicKey">The full public-key line (<c>&lt;type&gt; &lt;base64&gt; &lt;comment&gt;</c>).</param>
public sealed record SshHostKeyFingerprint(string KeyType, string Fingerprint, string PublicKey);
