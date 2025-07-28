using System.Runtime.Versioning;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

[SupportedOSPlatform("linux")]
public class LinuxKeyStorage : IKeyStorage
{
    private static string ConfigDirectoryPath => Path.Combine(
        "/etc", "opt", "eryph", "guest-services");

    private static string ClientKeyPath => Path.Combine(ConfigDirectoryPath, "id_egs.pub");

    private static string PrivateDirectoryPath => Path.Combine(ConfigDirectoryPath, "private");

    private static string HostKeyPath => Path.Combine(PrivateDirectoryPath, "egs_host_key");

    public async Task<IKeyPair?> GetClientKeyAsync()
    {
        if (!File.Exists(ClientKeyPath))
            return null;

        var keyBytes = await File.ReadAllBytesAsync(ClientKeyPath);
        var key = KeyPair.ImportKeyBytes(keyBytes);
        
        return key;
    }

    public async Task SetClientKeyAsync(IKeyPair keyPair)
    {
        Directory.CreateDirectory(ConfigDirectoryPath);

        if (Path.Exists(ClientKeyPath))
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

    private static void EnsurePrivateDirectory()
    {
        Directory.CreateDirectory(ConfigDirectoryPath);

        if (!Directory.Exists(PrivateDirectoryPath))
        {
            Directory.CreateDirectory(PrivateDirectoryPath);
            File.SetUnixFileMode(PrivateDirectoryPath, GetDirectoryFileMode());
            return;
        }

        if (File.GetUnixFileMode(PrivateDirectoryPath) == GetDirectoryFileMode())
            return;

        File.SetUnixFileMode(PrivateDirectoryPath, GetDirectoryFileMode());
        if (File.Exists(HostKeyPath))
        {
            // The key might be compromised -> delete it
            File.Delete(HostKeyPath);
        }
    }

    private static UnixFileMode GetDirectoryFileMode()
    {
        return UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    }
}
