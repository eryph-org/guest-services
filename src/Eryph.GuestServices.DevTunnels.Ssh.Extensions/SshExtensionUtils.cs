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

}