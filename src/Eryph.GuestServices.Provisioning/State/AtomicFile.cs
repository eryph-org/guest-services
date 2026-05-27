using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.State;

/// <summary>
/// Helpers for the write-temp-then-replace pattern the file-backed stores use.
///
/// On Windows, replacing an existing file (<c>File.Move(.., overwrite: true)</c> →
/// <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c>) can transiently fail with
/// <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/> when
/// antivirus (Defender real-time scan of the just-written file) or a concurrent
/// reader briefly holds the destination open without <c>FILE_SHARE_DELETE</c>.
/// state.json is rewritten after every module, so this race is hit in practice —
/// it crashed a real Windows Server 2025 provisioning run. Retrying the replace
/// (and letting readers share delete) makes the stores resilient to it.
/// </summary>
internal static class AtomicFile
{
    public static async Task ReplaceWithRetryAsync(
        string source,
        string destination,
        ILogger logger,
        CancellationToken cancellationToken,
        int maxAttempts = 6)
    {
        var delay = TimeSpan.FromMilliseconds(50);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < maxAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Replacing {Destination} failed (attempt {Attempt}/{Max}); retrying in {Delay}ms",
                    destination, attempt, maxAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }
    }
}
