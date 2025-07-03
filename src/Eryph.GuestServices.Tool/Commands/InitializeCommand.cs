using System.Security.AccessControl;
using System.Security.Principal;
using Eryph.GuestServices.Core;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Keys;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

internal class InitializeCommand : Command<InitializeCommand.Settings>
{
    public class Settings : CommandSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        Registration.Register(Constants.ServiceId, Constants.ServiceName);

        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "guest-services");
        Directory.CreateDirectory(configPath);

        var privateConfigPath = Path.Combine(configPath, "private");
        EnsureDirectory(privateConfigPath);
        
        var keyFilePath = Path.Combine(privateConfigPath, "id_egs");
        if (Path.Exists(keyFilePath))
        {
            AnsiConsole.WriteLine("The SSH key already exists.");
            return 0;
        }

        var keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        KeyPair.ExportPrivateKeyFile(keyPair, keyFilePath, keyFormat: KeyFormat.OpenSsh);
        KeyPair.ExportPublicKeyFile(keyPair, Path.Combine(privateConfigPath, "id_egs.pub"), KeyFormat.Ssh);

        return 0;
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
