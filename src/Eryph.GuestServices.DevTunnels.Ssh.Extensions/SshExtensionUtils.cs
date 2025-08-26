using System.Text.Json;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

/// <summary>
/// Shared utilities for SSH extensions including JSON serialization and path handling.
/// </summary>
public static class SshExtensionUtils
{
    /// <summary>
    /// Standard JSON serializer options used for file transfer communication.
    /// Uses camelCase naming policy to match client expectations.
    /// </summary>
    public static readonly JsonSerializerOptions FileTransferOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Normalizes a file path for SSH communication by converting all path separators to forward slashes.
    /// This ensures consistent path handling across Windows and Unix-like systems.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized path with forward slashes as separators.</returns>
    public static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}