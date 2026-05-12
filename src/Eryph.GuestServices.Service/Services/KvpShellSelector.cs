using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

/// <summary>
/// Resolves the shell with priority KVP &gt; SSH-sent env &gt; platform default.
/// </summary>
internal sealed class KvpShellSelector(
    IGuestDataExchange dataExchange,
    ILogger<KvpShellSelector> logger) : IShellSelector
{
    public async Task<ShellSelection> SelectAsync(
        ShellOverride sshOverride,
        CancellationToken cancellation)
    {
        try
        {
            var external = await dataExchange.GetExternalDataAsync();
            if (external.TryGetValue(Constants.ShellKey, out var kvpShell)
                && !string.IsNullOrWhiteSpace(kvpShell))
            {
                external.TryGetValue(Constants.ShellArgsKey, out var kvpArgs);
                logger.LogDebug("Using shell from KVP: {Command}", kvpShell);
                return new ShellSelection(kvpShell, kvpArgs ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            // KVP read failures must not block shell startup. Fall through to
            // the env/default chain.
            logger.LogWarning(ex, "Failed to read shell configuration from Hyper-V data exchange.");
        }

        var fallback = DefaultShellSelector.SelectFromEnvOrDefault(sshOverride);
        logger.LogDebug("Using shell from env/default: {Command}", fallback.Command);
        return fallback;
    }
}
