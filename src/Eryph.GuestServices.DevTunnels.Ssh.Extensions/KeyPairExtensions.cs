using System.Security.Cryptography;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public static class KeyPairExtensions
{
    public static string GetFingerPrint(this IKeyPair keyPair)
    {
        var keyBytes = keyPair.GetPublicKeyBytes();
        var hashBytes = SHA256.HashData(keyBytes.Memory.Span);
        return string.Join(":", hashBytes.Select(b => $"{b:x2}"));
    }
}
