using System.Security.AccessControl;
using System.Security.Principal;
using Eryph.GuestServices.Client;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;

namespace Eryph.GuestServices.Tool;

// The per-user managed client key for the elevation-exempt 'eryph' command group.
//
// ClientKeyHelper's id_egs lives under ProgramData and is created by the elevated
// 'initialize' command with an ACL granting only Administrators/SYSTEM — correct
// for the egs service and the VM-level (elevated) flow, but a non-elevated
// operator cannot even read it, and Windows OpenSSH refuses to load a key the
// running user has no access to.
//
// This key instead lives in the operator's own profile and is readable only by
// the operator (plus SYSTEM/Administrators) — exactly what Windows OpenSSH
// requires of a private key it loads as the user. It is created on demand so the
// eryph commands work without a separate elevated init step.
public static class UserClientKeyHelper
{
    private static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ".eryph",
        "guest-services");

    private static string PrivateDirectory => Path.Combine(ConfigDirectory, "private");

    public static string PrivateKeyPath => Path.Combine(PrivateDirectory, "id_egs");

    private static string PublicKeyPath => Path.Combine(PrivateDirectory, "id_egs.pub");

    public static async Task<IKeyPair?> GetKeyPairAsync()
    {
        EnsurePrivateDirectory();
        if (!File.Exists(PrivateKeyPath))
            return null;

        var bytes = await File.ReadAllBytesAsync(PrivateKeyPath);
        return KeyPair.ImportKeyBytes(bytes);
    }

    // Returns the existing key or creates a fresh one. The eryph commands run as
    // the operator and may create the key on demand; the file inherits the
    // private directory's user-only ACL (see GetDirectorySecurity).
    public static async Task<IKeyPair> EnsureKeyPairAsync()
    {
        var existing = await GetKeyPairAsync();
        if (existing is not null)
            return existing;

        var keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        // Normalize CRLF -> LF: DevTunnels exports the key with Windows line
        // endings, which the MSYS ssh in embedded shells cannot load. See
        // OpenSshKeyBytes.
        var privateKeyBytes = OpenSshKeyBytes.NormalizeLineEndingsToLf(
            KeyPair.ExportPrivateKeyBytes(keyPair, keyFormat: KeyFormat.OpenSsh));
        await File.WriteAllBytesAsync(PrivateKeyPath, privateKeyBytes);

        // OpenSSH wants the public key alongside the private key.
        var publicKeyBytes = OpenSshKeyBytes.NormalizeLineEndingsToLf(
            KeyPair.ExportPublicKeyBytes(keyPair, keyFormat: KeyFormat.Ssh));
        await File.WriteAllBytesAsync(PublicKeyPath, publicKeyBytes);

        return keyPair;
    }

    private static void EnsurePrivateDirectory()
    {
        Directory.CreateDirectory(ConfigDirectory);

        var directoryInfo = new DirectoryInfo(PrivateDirectory);
        var security = GetDirectorySecurity();
        if (!directoryInfo.Exists)
        {
            directoryInfo.Create(security);
            return;
        }

        directoryInfo.SetAccessControl(security);
    }

    private static DirectorySecurity GetDirectorySecurity()
    {
        var security = new DirectorySecurity();
        using var identity = WindowsIdentity.GetCurrent();

        void Grant(IdentityReference id) => security.AddAccessRule(new FileSystemAccessRule(
            id,
            FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // Only the operator (plus SYSTEM/Administrators) — no entry for other
        // users. Keys created in this directory inherit these rules, so Windows
        // OpenSSH will accept them.
        Grant(identity.User!);
        Grant(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Grant(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        // Protect from inheritance and do not copy the (broader) inherited rules.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        return security;
    }
}
