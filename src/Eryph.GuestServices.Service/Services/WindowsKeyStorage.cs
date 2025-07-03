using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

[SupportedOSPlatform("windows")]
public class WindowsKeyStorage(IHostKeyGenerator hostKeyGenerator) : IKeyStorage
{
    public IKeyPair? GetClientKey()
    {
        var keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "guest-services",
            "id_egs.pub");

        if (!File.Exists(keyFilePath))
            return null;
        
        return KeyPair.ImportKeyFile(keyFilePath);   
    }

    public IKeyPair GetHostKey()
    {
        var directoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "guest-services",
            "private");

        EnsureDirectory(directoryPath);

        // TODO fix file name
        var keyFilePath = Path.Combine(directoryPath, "egs_host_key");
        if (File.Exists(keyFilePath))
        {
            try
            {
                return KeyPair.ImportKeyFile(keyFilePath);
            }
            catch (Exception ex)
            {
                File.Delete(keyFilePath);
                throw new InvalidOperationException(
                    $"Could not delete existing host key file at {keyFilePath}.", ex);
            }
        }

        var keyPair = hostKeyGenerator.GenerateHostKey();
        KeyPair.ExportPrivateKeyFile(keyPair, keyFilePath);
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

        if (IsDirectorySecurityValid(directoryInfo.GetAccessControl()))
            return;

        directoryInfo.SetAccessControl(security);
        var keyFilePath = Path.Combine(directoryPath, "egs_host_key");
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
