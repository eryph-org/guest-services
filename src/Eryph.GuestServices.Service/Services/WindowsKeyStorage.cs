using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

[SupportedOSPlatform("windows")]
public class WindowsKeyStorage : IKeyStorage
{
    private static string ConfigDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "eryph", "guest-services");

    private static string ClientKeyPath => Path.Combine(ConfigDirectoryPath, "id_egs.pub");

    private static string PrivateDirectoryPath => Path.Combine(ConfigDirectoryPath, "private");

    private static string HostKeyPath => Path.Combine(PrivateDirectoryPath, "egs_host_key");

    public async Task<IKeyPair?> GetClientKeyAsync()
    {
        if (!File.Exists(ClientKeyPath))
            return null;

        var keyBytes = await File.ReadAllBytesAsync(ClientKeyPath);
        return KeyPair.ImportKeyBytes(keyBytes);
    }

    public async Task SetClientKeyAsync(IKeyPair keyPair)
    {
        if (File.Exists(ClientKeyPath))
            throw new InvalidOperationException("Cannot update the client key. It already exists.");

        var keyBytes = KeyPair.ExportPublicKeyBytes(keyPair);
        await File.WriteAllBytesAsync(ClientKeyPath, keyBytes);
    }

    public async Task<IKeyPair?> GetHostKeyAsync()
    {
        EnsurePrivateDirectory();

        if (!File.Exists(HostKeyPath))
            return null;

        var keyBytes = await File.ReadAllBytesAsync(HostKeyPath);

        try
        {
            return KeyPair.ImportKeyBytes(keyBytes);
        }
        catch
        {
            File.Delete(HostKeyPath);
            return null;
        }
    }

    public async Task SetHostKeyAsync(IKeyPair keyPair)
    {
        EnsurePrivateDirectory();

        if (File.Exists(HostKeyPath))
            throw new InvalidOperationException("Cannot update the host key. It already exists.");
        
        var keyBytes = KeyPair.ExportPrivateKeyBytes(keyPair);
        await File.WriteAllBytesAsync(HostKeyPath, keyBytes);
    }

    private static void EnsureConfigDirectory()
    {
        if (Directory.Exists(ConfigDirectoryPath))
            return;
        
        Directory.CreateDirectory(ConfigDirectoryPath);
    }

    private static void EnsurePrivateDirectory()
    {
        EnsureConfigDirectory();

        var directoryInfo = new DirectoryInfo(PrivateDirectoryPath);
        var security = GetDirectorySecurity();
        if (!directoryInfo.Exists)
        {
            directoryInfo.Create(security);
            return;
        }

        if (IsDirectorySecurityValid(directoryInfo.GetAccessControl()))
            return;

        directoryInfo.SetAccessControl(security);
        var keyFilePath = Path.Combine(HostKeyPath, "egs_host_key");
        if (File.Exists(keyFilePath))
        {
            // The key might be compromised -> delete it
            File.Delete(keyFilePath);
        }
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

    private static bool IsDirectorySecurityValid(DirectorySecurity security)
    {
        if (!security.AreAccessRulesProtected)
            return false;

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (rules.Count != 2)
            return false;

        var adminId = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemId = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        return rules.Cast<AuthorizationRule>()
            .All(r => r.IdentityReference == adminId || r.IdentityReference == systemId);
    }
}
