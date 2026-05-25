using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure;

namespace Eryph.GuestServices.Provisioning.Windows.Licensing;

/// <summary>
/// Thin wrapper around the <c>SoftwareLicensingProduct</c> WMI class —
/// the same surface cloudbase-init queries to identify the active
/// KMS-client product and its license family.
/// </summary>
/// <remarks>
/// Windows ships a row per registered license entry. The one with
/// <c>VOLUME_KMSCLIENT</c> in its Description AND a non-empty
/// <c>PartialProductKey</c> is the "current" product — its
/// <c>LicenseFamily</c> drives our AVMA/KMS table lookup.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class CimLicensing
{
    private const string CimNamespace = @"root\cimv2";

    // Microsoft Windows ApplicationId. SoftwareLicensingProduct rows for
    // Office / other Microsoft products use different ApplicationIds, but
    // licensing this module targets Windows itself.
    private const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    public static KmsClientProduct? FindActiveKmsClientProduct()
    {
        using var session = CimSession.Create(null);

        // Filter at WMI to avoid materialising every product row — the SLP
        // table can be large on guests with many add-on activations.
        const string query =
            "SELECT ApplicationID, Description, LicenseFamily, PartialProductKey, " +
            "EvaluationEndDate, Name FROM SoftwareLicensingProduct WHERE LicenseIsAddon = False";

        foreach (var instance in session.QueryInstances(CimNamespace, "WQL", query))
        {
            using (instance)
            {
                var appId = GetString(instance, "ApplicationID");
                if (!string.Equals(appId, WindowsApplicationId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var description = GetString(instance, "Description") ?? string.Empty;
                if (!description.Contains("VOLUME_KMSCLIENT", StringComparison.Ordinal))
                    continue;

                // PartialProductKey is the last-5 of the currently-installed
                // key. Empty means "this row is a candidate, not the active
                // product" — skip in that case.
                var partialKey = GetString(instance, "PartialProductKey") ?? string.Empty;
                var isCurrent = partialKey.Length > 0;
                if (!isCurrent)
                    continue;

                var licenseFamily = GetString(instance, "LicenseFamily") ?? string.Empty;
                var evaluationEndDate = GetString(instance, "EvaluationEndDate") ?? string.Empty;

                return new KmsClientProduct
                {
                    Description = description,
                    LicenseFamily = licenseFamily,
                    EvaluationEndDate = evaluationEndDate,
                };
            }
        }

        return null;
    }

    private static string? GetString(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value as string;
}

internal sealed record KmsClientProduct
{
    public string Description { get; init; } = string.Empty;
    public string LicenseFamily { get; init; } = string.Empty;

    /// <summary>
    /// WMI surfaces this as a CIM datetime string. The sentinel
    /// <c>16010101000000.000000-000</c> means "never expires" (i.e. a
    /// non-evaluation product). Any other value indicates an evaluation
    /// license whose <c>slmgr /rearm</c> matters.
    /// </summary>
    public string EvaluationEndDate { get; init; } = string.Empty;

    public bool IsEvaluation =>
        !string.Equals(EvaluationEndDate, "16010101000000.000000-000", StringComparison.Ordinal)
        || Description.Contains("TIMEBASED_EVAL", StringComparison.Ordinal);
}
