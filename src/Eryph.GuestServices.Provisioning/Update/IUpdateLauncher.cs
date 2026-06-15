using System.Diagnostics;
using System.Runtime.Versioning;
using Eryph.GuestServices.Core;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>
/// Launches the out-of-process updater that swaps the running agent's binaries.
/// The updater is the <em>staged</em> <c>egs-service.exe</c> invoked with the
/// <c>apply-update</c> verb, so the executable doing the copy is never the one
/// being overwritten (eryph-zero's self-install pattern).
/// </summary>
public interface IUpdateLauncher
{
    /// <summary>
    /// Spawns the staged updater, detached, and returns. The caller then stops
    /// the host; the updater stops the service, swaps <c>--to</c> with the
    /// staged payload, and restarts the service.
    /// </summary>
    void Launch(UpdatePlan plan);
}

/// <summary>
/// Default <see cref="IUpdateLauncher"/>. On Windows a plain detached
/// <see cref="Process.Start(ProcessStartInfo)"/> is sufficient: Windows does
/// not kill orphaned children when their parent (the service) stops, and a
/// service is not in a kill-on-job-close job — so the updater outlives the
/// service stop it triggers. It runs from the staging dir, never from the
/// install dir it overwrites.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateLauncher(ILogger<UpdateLauncher> logger) : IUpdateLauncher
{
    public void Launch(UpdatePlan plan)
    {
        // The install dir is where the running agent lives; the updater copies
        // the staged payload over it.
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var stagedAgent = Path.Combine(plan.StagingDirectory, "egs-service.exe");

        var psi = new ProcessStartInfo
        {
            FileName = stagedAgent,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = plan.StagingDirectory,
        };
        psi.ArgumentList.Add("apply-update");
        psi.ArgumentList.Add("--from");
        psi.ArgumentList.Add(plan.StagingDirectory);
        psi.ArgumentList.Add("--to");
        psi.ArgumentList.Add(installDir);
        psi.ArgumentList.Add("--service");
        psi.ArgumentList.Add(Constants.DaemonServiceName);

        logger.LogInformation(
            "Launching updater {Exe} to apply {Version} (install dir {InstallDir}).",
            stagedAgent, plan.TargetVersion, installDir);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start updater process '{stagedAgent}'.");
    }
}
