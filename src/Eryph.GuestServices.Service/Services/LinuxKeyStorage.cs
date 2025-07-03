using System.Runtime.Versioning;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

[SupportedOSPlatform("linux")]
public class LinuxKeyStorage(IHostKeyGenerator hostKeyGenerator) : IKeyStorage
{
    public IKeyPair? GetClientKey()
    {
        var keyFilePath = Path.Combine(
            "/etc", "opt", "eryph", "guest-services", "id_egs.pub");

        return !File.Exists(keyFilePath) ? null : KeyPair.ImportKeyFile(keyFilePath);
    }

    public IKeyPair GetHostKey()
    {
        var directoryPath = Path.Combine(
            "/etc", "opt", "eryph", "guest-services", "private");

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
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
            File.SetUnixFileMode(directoryPath, GetDirectoryFileMode());
            return;
        }

        if (File.GetUnixFileMode(directoryPath) == GetDirectoryFileMode())
            return;

        File.SetUnixFileMode(directoryPath, GetDirectoryFileMode());
        var keyFilePath = Path.Combine(directoryPath, "egs_host_key");
        if (File.Exists(keyFilePath))
        {
            // The key might be compromised -> delete it
            File.Delete(keyFilePath);
        }
    }

    private static UnixFileMode GetDirectoryFileMode()
    {
        return UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    }
}
