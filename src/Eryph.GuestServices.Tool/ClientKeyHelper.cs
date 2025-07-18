using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;

namespace Eryph.GuestServices.Tool;

public static class ClientKeyHelper
{
    public static IKeyPair? GetPrivateKey()
    {
        var keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "guest-services",
            "private",
            "id_egs");

        return !Path.Exists(keyFilePath) ? null : KeyPair.ImportKeyFile(keyFilePath);
    }

    public static IKeyPair CreatePrivateKey()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "guest-services");
        Directory.CreateDirectory(configPath);

        var privateConfigPath = Path.Combine(configPath, "private");
        EnsureDirectory(privateConfigPath);

        var keyFilePath = Path.Combine(privateConfigPath, "id_egs");
        var keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        KeyPair.ExportPrivateKeyFile(keyPair, keyFilePath, keyFormat: KeyFormat.OpenSsh);
        
        return keyPair;
    }

    private static void EnsureDirectory(string directoryPath)
    {
        var directoryInfo = new DirectoryInfo(directoryPath);
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
