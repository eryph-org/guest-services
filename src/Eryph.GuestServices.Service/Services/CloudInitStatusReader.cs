using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

internal interface ICloudInitStatusReader
{
    // The cloud-init "status" value (done/running/error/not run/disabled), or
    // null when cloud-init is not present / produced no parseable output.
    Task<string?> GetStatusAsync(CancellationToken cancellationToken);
}

// Reads cloud-init's status via `cloud-init status --format json`. Used on Linux
// guests, where cloud-init (not egs) does the provisioning.
internal sealed class CloudInitStatusReader(ILogger<CloudInitStatusReader> logger) : ICloudInitStatusReader
{
    public async Task<string?> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cloud-init",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("status");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("json");

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            // `cloud-init status` reflects the state in its exit code (non-zero
            // for error/degraded) but still prints the JSON, so parse stdout
            // regardless of the exit code.
            //
            // Drain stdout AND stderr concurrently before waiting for exit:
            // reading only stdout risks a deadlock if the child fills the stderr
            // pipe and blocks on the write. We don't use stderr, but it must
            // still be drained.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(stdout))
                return null;

            using var document = JsonDocument.Parse(stdout);
            return document.RootElement.TryGetProperty("status", out var status)
                ? status.GetString()
                : null;
        }
        catch (Win32Exception)
        {
            // cloud-init is not installed on this guest.
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read cloud-init status");
            return null;
        }
    }
}
