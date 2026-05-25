using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Activates Windows on the guest. Default-on behaviours (no <c>license:</c>
/// block required):
/// <list type="bullet">
///   <item>Install the AVMA key for the guest's OS edition. On a Datacenter
///   Hyper-V host this activates the VM immediately; on any other host the
///   key sits dormant and does no harm — so AVMA-on-by-default is safe.</item>
///   <item>Run <c>slmgr /rearm</c> if the active product is an evaluation
///   edition. Non-eval guests skip rearm so no rearm slot is burned.</item>
/// </list>
/// On Azure, the activation path (product key / AVMA / KMS / activate) is
/// skipped because Azure handles activation natively; rearm still runs on
/// evaluation editions because Azure does not extend evaluation grace
/// periods. <c>license.force: true</c> overrides the Azure skip.
/// </summary>
[Stage(Stage.Config, Order = 5, Frequency = ModuleFrequency.PerInstance)]
internal sealed class LicensingModule(ILogger<LicensingModule> logger) : IModule
{
    // Cloud-name marker populated by AzureDataSource on PlatformMetadata.
    // Stable across datasource refactors (the source name might change for
    // diagnostics; the cloud name is the contract). Lowercase mirrors
    // cloud-init's well-known cloud_name values.
    private const string AzureCloudName = "azure";

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var license = userData.CloudConfig.License ?? new LicenseConfig();

        var explicitKey = NonBlank(license.ProductKey);
        var explicitKmsHost = NonBlank(license.KmsHost);
        var activate = license.Activate ?? false;

        // Defaults differ from earlier revisions of this module: AVMA and
        // rearm are on by default because they are safe no-ops outside their
        // applicable edition (AVMA: silent skip on non-Server SKUs; rearm:
        // silent skip on non-eval guests).
        var setAvma = license.SetAvma ?? true;
        var setKms = license.SetKms ?? false;
        var rearm = license.Rearm ?? true;
        var force = license.Force ?? false;

        // Track which fields were defaulted vs explicitly requested — a null
        // resolution silently falls through on a defaulted SetAvma but is a
        // hard failure when the operator typed `set_avma: true` themselves
        // (they expect a key and would want loud feedback otherwise).
        var setAvmaExplicit = license.SetAvma == true;
        var setKmsExplicit = license.SetKms == true;

        var isAzure = IsAzureDataSource(context);
        var skipActivation = isAzure && !force;

        // Phase 1 — activation work (product key, KMS host, activate).
        // Skipped on Azure unless the operator forces, because Azure has its
        // own KMS (kms.core.windows.net) and our slmgr would only add noise.
        var activationDidSomething = false;
        if (!skipActivation)
        {
            var activationOutcome = await TryApplyActivationAsync(
                context, explicitKey, explicitKmsHost, activate,
                setAvma, setAvmaExplicit, setKms, setKmsExplicit,
                cancellationToken).ConfigureAwait(false);
            if (activationOutcome is not null)
                return activationOutcome;  // propagate Failed / Reboot
            activationDidSomething =
                explicitKey is not null || explicitKmsHost is not null || activate
                || setAvmaExplicit || setKmsExplicit
                || setAvma || setKms;
        }
        else
        {
            logger.LogInformation(
                "Datasource is Azure; skipping activation path (set license.force=true to override). Rearm still considered.");
        }

        // Phase 2 — rearm, which is a separate concern from activation.
        // Runs even on Azure: evaluation grace periods are an OS-level
        // mechanism that Azure neither extends nor refreshes.
        if (rearm)
        {
            var rearmOutcome = await TryRearmIfEvaluationAsync(context, cancellationToken).ConfigureAwait(false);
            if (rearmOutcome is not null)
                return rearmOutcome;  // propagate Failed / Reboot
        }

        if (!activationDidSomething && !rearm)
            logger.LogDebug("license: nothing to do this run.");
        return ModuleOutcome.Ok();
    }

    private async Task<ModuleOutcome?> TryApplyActivationAsync(
        IModuleContext context,
        string? explicitKey,
        string? explicitKmsHost,
        bool activate,
        bool setAvma, bool setAvmaExplicit,
        bool setKms, bool setKmsExplicit,
        CancellationToken cancellationToken)
    {
        // Resolve the product key. Priority: explicit > AVMA > KMS auto.
        // Defaulted SetAvma / SetKms returning null is a silent no-op (the
        // guest may be a client SKU or an edition with no key in our table).
        // Explicit `set_avma: true` returning null is a loud failure — the
        // operator expects a key and silence would mislead.
        string? resolvedKey = explicitKey;
        if (resolvedKey is null && setAvma)
        {
            resolvedKey = await context.Os.ResolveVolumeActivationKeyAsync(
                VolumeActivationKeyType.Avma, cancellationToken).ConfigureAwait(false);
            if (resolvedKey is null && setAvmaExplicit)
                return ModuleOutcome.Fail("license: no AVMA key known for this OS edition");
            if (resolvedKey is not null)
                logger.LogInformation("Resolved AVMA key for this OS edition.");
        }
        if (resolvedKey is null && setKms)
        {
            resolvedKey = await context.Os.ResolveVolumeActivationKeyAsync(
                VolumeActivationKeyType.Kms, cancellationToken).ConfigureAwait(false);
            if (resolvedKey is null && setKmsExplicit)
                return ModuleOutcome.Fail("license: no KMS key known for this OS edition");
            if (resolvedKey is not null)
                logger.LogInformation("Resolved KMS-client key for this OS edition.");
        }

        // KMS auto-discovery: when set_kms is requested but no host is
        // supplied, clear the configured host so DNS SRV discovery takes
        // over (the corporate / Azure-Stack / SPLA norm).
        var clearKmsHost = setKms && explicitKmsHost is null;

        // If no key resolved AND nothing else to do at the activation layer,
        // silently no-op — defaulted AVMA on a client SKU lands here.
        var hasActivationWork =
            resolvedKey is not null || explicitKmsHost is not null || clearKmsHost || activate;
        if (!hasActivationWork)
        {
            logger.LogDebug(
                "No activation work to do (no key resolved and no KMS host / activate flag).");
            return null;
        }

        var spec = new LicenseSpec
        {
            ProductKey = resolvedKey,
            KmsHost = explicitKmsHost,
            ClearKmsHost = clearKmsHost,
            Activate = activate,
        };

        try
        {
            await context.Os.ApplyLicenseAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Licensing apply failed.");
            return ModuleOutcome.Fail($"license: {ex.Message}", ex);
        }

        logger.LogInformation(
            "Licensing applied (productKey={Key}, kmsHost={Host}, clearKms={Clear}, activate={Activate}).",
            resolvedKey is null ? "<unchanged>" : $"***{resolvedKey[Math.Max(0, resolvedKey.Length - 4)..]}",
            explicitKmsHost ?? (clearKmsHost ? "<cleared>" : "<unchanged>"),
            clearKmsHost,
            activate);
        return null;
    }

    private async Task<ModuleOutcome?> TryRearmIfEvaluationAsync(
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        bool isEval;
        try
        {
            isEval = await context.Os.IsEvaluationLicenseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failing the eval probe shouldn't bring the whole module down —
            // rearm is best-effort. Log + continue (no rearm).
            logger.LogWarning(ex, "IsEvaluationLicenseAsync failed; skipping rearm.");
            return null;
        }
        if (!isEval)
        {
            logger.LogDebug("Active product is not an evaluation; rearm skipped.");
            return null;
        }

        RearmResult result;
        try
        {
            result = await context.Os.RearmLicenseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "slmgr /rearm failed.");
            return ModuleOutcome.Fail($"license rearm: {ex.Message}", ex);
        }

        if (result.RebootRequired)
        {
            logger.LogInformation("Evaluation rearmed; reboot required.");
            return ModuleOutcome.Reboot("Evaluation rearm requires reboot.");
        }

        logger.LogInformation("Evaluation rearmed.");
        return null;
    }

    private static bool IsAzureDataSource(IModuleContext context) =>
        string.Equals(
            context.DataSource.PlatformMetadata?.CloudName,
            AzureCloudName,
            StringComparison.OrdinalIgnoreCase);

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
