using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Validation;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Cloud-init <c>growpart</c> for Windows: grows targeted volumes into any
/// unallocated space behind them. Mirrors cloud-init's schema
/// (<c>mode</c>, <c>devices</c>) and cloudbase-init's behaviour of extending
/// volumes via the storage WMI provider — see <c>CimStorage</c>.
/// </summary>
/// <remarks>
/// Runs per-boot: the host can resize the underlying VHD between reboots,
/// so a per-instance gate would miss the most common operator workflow.
/// Cloud-init's cc_growpart is also <c>per-always</c>.
/// </remarks>
[Stage(Stage.Network, Order = 0, Frequency = ModuleFrequency.PerBoot)]
internal sealed class GrowpartModule(ILogger<GrowpartModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var growpart = userData.CloudConfig.Growpart ?? new GrowpartConfig();
        var mode = (growpart.Mode ?? "auto").Trim().ToLowerInvariant();

        // The schema validator accepts auto / off / false. Anything else
        // is an operator typo or a Linux-only mode (growpart / gpart) we
        // cannot honour on Windows — log and skip rather than running the
        // auto branch by accident. The CLI validator catches this at
        // validate-time; the no-op below is the runtime safety net for
        // catlets that bypass validate (older fodder, datasource overrides).
        if (mode is "off" or "false")
        {
            logger.LogInformation("growpart mode is '{Mode}'; skipping volume extension.", mode);
            return ModuleOutcome.Ok();
        }
        if (mode != "auto")
        {
            logger.LogWarning(
                "growpart mode '{Mode}' is not supported on Windows; only 'auto' and 'off' are understood. Skipping.",
                mode);
            return ModuleOutcome.Ok();
        }

        var devices = growpart.Devices is { Count: > 0 } d ? d : ["/"];
        var (filter, hasAll) = ResolveDriveLetterFilter(devices);
        if (!hasAll && filter.Count == 0)
        {
            logger.LogWarning(
                "growpart.devices resolved to an empty target set ({Raw}); nothing to do.",
                string.Join(", ", devices));
            return ModuleOutcome.Ok();
        }

        IReadOnlyList<VolumeExtendResult> results;
        try
        {
            results = await context.Os
                .ExtendVolumesAsync(hasAll ? null : filter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "growpart failed to enumerate or resize volumes.");
            return ModuleOutcome.Fail($"growpart: {ex.Message}", ex);
        }

        var extended = 0;
        foreach (var r in results)
        {
            if (r.Extended)
            {
                extended++;
                logger.LogInformation(
                    "Extended volume {Letter} ({VolumeId}) from {Before:N0} to {After:N0} bytes.",
                    r.DriveLetter?.ToString() ?? "(no letter)", r.VolumeId, r.SizeBefore, r.SizeAfter);
            }
            else
            {
                logger.LogDebug(
                    "Volume {Letter} ({VolumeId}) considered; size unchanged at {Size:N0} bytes.",
                    r.DriveLetter?.ToString() ?? "(no letter)", r.VolumeId, r.SizeBefore);
            }
        }

        logger.LogInformation("growpart finished — {Extended} volume(s) grown.", extended);
        return ModuleOutcome.Ok();
    }

    // Lifts cloud-init's `devices:` list onto the OS layer's "uppercase
    // drive letters OR no filter (= all)" abstraction. The shape /
    // grammar of each entry is decided by `GrowpartGrammar.ParseDevice`
    // in the model library; this method just walks the validated entries
    // and resolves "/" to %SystemDrive% at runtime (which the model
    // library cannot — it doesn't know the host's system drive).
    private static (HashSet<char> Letters, bool HasAll) ResolveDriveLetterFilter(IReadOnlyList<string> devices)
    {
        var letters = new HashSet<char>();
        var hasAll = false;
        foreach (var raw in devices)
        {
            var parsed = GrowpartGrammar.ParseDevice(raw ?? string.Empty);
            if (parsed.IsFail)
            {
                // Bad entries already surfaced at validate-time; skipping
                // here keeps the module robust against fodder that bypassed
                // validate (older catlets, manual datasource overrides).
                continue;
            }
            var target = parsed.SuccessToSeq().Head;
            switch (target.Kind)
            {
                case GrowpartGrammar.DeviceKind.All:
                    hasAll = true;
                    break;
                case GrowpartGrammar.DeviceKind.SystemDrive:
                    var sysLetter = SystemDriveLetter();
                    if (sysLetter is not null) letters.Add(sysLetter.Value);
                    break;
                case GrowpartGrammar.DeviceKind.DriveLetter:
                    if (target.DriveLetter is not null) letters.Add(target.DriveLetter.Value);
                    break;
            }
        }
        return (letters, hasAll);
    }

    private static char? SystemDriveLetter()
    {
        // %SystemDrive% is "C:" on every supported Windows install but we
        // still resolve it dynamically so reimaged guests with a non-C:
        // system drive work correctly.
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrWhiteSpace(sysDrive)) return null;
        var ch = sysDrive[0];
        if (ch is >= 'a' and <= 'z') ch = char.ToUpperInvariant(ch);
        return ch is >= 'A' and <= 'Z' ? ch : null;
    }
}
