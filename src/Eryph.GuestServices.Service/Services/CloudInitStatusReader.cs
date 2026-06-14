using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

// The outcome of probing cloud-init. Distinguishes "cloud-init isn't installed"
// (a permanent condition — stop polling) from "installed but no parseable
// status yet" (transient — cloud-init may still be starting; keep polling).
internal readonly record struct CloudInitProbe(bool Installed, string? Status)
{
    public static readonly CloudInitProbe NotInstalled = new(false, null);

    // Installed; Status is the cloud-init value (done/running/error/...) or null
    // when no parseable status was produced yet.
    public static CloudInitProbe Running(string? status) => new(true, status);
}

internal interface ICloudInitStatusReader
{
    Task<CloudInitProbe> ReadAsync(CancellationToken cancellationToken);
}

// Reads cloud-init's status via `cloud-init status --format json`. Used on Linux
// guests, where cloud-init (not egs) does the provisioning.
internal sealed class CloudInitStatusReader(ILogger<CloudInitStatusReader> logger) : ICloudInitStatusReader
{
    public async Task<CloudInitProbe> ReadAsync(CancellationToken cancellationToken)
    {
        Process? process = null;
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

            process = Process.Start(startInfo);
            if (process is null)
                return CloudInitProbe.NotInstalled;

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

            // Installed but nothing parseable yet — transient, keep polling.
            if (string.IsNullOrWhiteSpace(stdout))
                return CloudInitProbe.Running(null);

            using var document = JsonDocument.Parse(stdout);
            return CloudInitProbe.Running(
                document.RootElement.TryGetProperty("status", out var status)
                    ? status.GetString()
                    : null);
        }
        catch (Win32Exception)
        {
            // cloud-init binary not found — a permanent condition. .NET surfaces
            // the launch failure as a Win32Exception on both platforms (Windows
            // ERROR_FILE_NOT_FOUND and Linux ENOENT alike), so this single catch
            // covers "not installed" everywhere.
            return CloudInitProbe.NotInstalled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Parse error / unexpected failure: treat as transient (installed,
            // no status yet) so the watcher retries instead of giving up.
            logger.LogDebug(ex, "Failed to read cloud-init status; will retry");
            return CloudInitProbe.Running(null);
        }
        finally
        {
            // Disposing a Process does not stop the child; on cancellation (or a
            // mid-read failure) kill it so it can't outlive the service.
            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort; the process may have exited between the check and the kill.
            }

            process?.Dispose();
        }
    }
}
