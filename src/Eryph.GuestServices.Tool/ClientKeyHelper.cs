using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;

namespace Eryph.GuestServices.Tool;

public static class ClientKeyHelper
{
    private static string ConfigDirectory =>  Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "eryph",
        "guest-services");

    private static string PrivateDirectory => Path.Combine(ConfigDirectory, "private");

    public static string PrivateKeyPath => Path.Combine(PrivateDirectory, "id_egs");

    private static string PublicKeyPath => Path.Combine(PrivateDirectory, "id_egs.pub");

    public static async Task<IKeyPair?> GetKeyPairAsync()
    {
        EnsurePrivateDirectory();
        if (!Path.Exists(PrivateKeyPath))
            return null;

        var bytes = await File.ReadAllBytesAsync(PrivateKeyPath);
        return KeyPair.ImportKeyBytes(bytes);
    }

    public static async Task<IKeyPair> CreateKeyPairAsync()
    {
        EnsurePrivateDirectory();

        var keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var privateKeyBytes = KeyPair.ExportPrivateKeyBytes(keyPair, keyFormat: KeyFormat.OpenSsh);
        await File.WriteAllBytesAsync(PrivateKeyPath, privateKeyBytes);

        // Also export the public key as OpenSSH requires it
        var publicKeyBytes = KeyPair.ExportPublicKeyBytes(keyPair, keyFormat: KeyFormat.Ssh);
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
        var directorySecurity = new DirectorySecurity();
        IdentityReference adminId = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var adminAccess = new FileSystemAccessRule(
            adminId,
            FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

        IdentityReference systemId = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var systemAccess = new FileSystemAccessRule(
            systemId,
            FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

        directorySecurity.AddAccessRule(adminAccess);
        directorySecurity.AddAccessRule(systemAccess);
        // Set the owner and the group to admins
        directorySecurity.SetAccessRuleProtection(true, true);

        return directorySecurity;
    }
}
