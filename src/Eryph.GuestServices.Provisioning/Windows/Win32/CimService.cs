using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// Thin wrapper around Win32_Service CIM methods (ChangeStartMode,
/// StartService, StopService). Cloudbase-init uses the SCM API directly
/// via osutils.set_service_start_mode / start_service / stop_service —
/// we mirror the same semantics without depending on sc.exe's argv-parsing
/// quirks (the documented <c>start= value</c> syntax requires two adjacent
/// argv tokens, which is fragile across .NET's ArgumentList serialiser).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CimService
{
    private const string CimNamespace = @"root\cimv2";

    public enum StartMode
    {
        Automatic,
        Manual,
        Disabled,
    }

    public static void ChangeStartMode(string serviceName, StartMode mode, ILogger logger)
    {
        using var session = CimSession.Create(null);
        using var instance = LookupService(session, serviceName)
            ?? throw new InvalidOperationException($"Service '{serviceName}' not found.");

        var startMode = mode switch
        {
            StartMode.Automatic => "Automatic",
            StartMode.Manual => "Manual",
            StartMode.Disabled => "Disabled",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported start mode."),
        };

        using var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("StartMode", startMode, CimType.String, CimFlags.None),
        };

        using var result = session.InvokeMethod(instance, "ChangeStartMode", parameters);
        var rc = ConvertToUInt32(result.ReturnValue.Value);
        // Win32_Service.ChangeStartMode return codes: 0=success, 1=not supported,
        // 2=access denied, 21=invalid parameter, 22=unknown service type, etc.
        // We surface non-zero values so the caller can log and decide; "0 = same as
        // requested" also counts as success per the documented contract.
        if (rc != 0)
            throw new InvalidOperationException(
                $"Win32_Service.ChangeStartMode({serviceName}, {startMode}) returned {rc}.");

        logger.LogDebug("Service {Name} StartMode set to {StartMode}.", serviceName, startMode);
    }

    public static void StartService(string serviceName, ILogger logger)
    {
        using var session = CimSession.Create(null);
        using var instance = LookupService(session, serviceName)
            ?? throw new InvalidOperationException($"Service '{serviceName}' not found.");

        var state = GetString(instance, "State") ?? string.Empty;
        if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "Start Pending", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Service {Name} already in state {State}; no-op.", serviceName, state);
            return;
        }

        using var result = session.InvokeMethod(instance, "StartService", new CimMethodParametersCollection());
        var rc = ConvertToUInt32(result.ReturnValue.Value);
        // Win32_Service.StartService returns 0 on success; 10 means "already
        // running" (race between our state check and the invoke — benign).
        if (rc != 0 && rc != 10)
            throw new InvalidOperationException(
                $"Win32_Service.StartService({serviceName}) returned {rc}.");

        logger.LogDebug("Service {Name} StartService rc={Rc}.", serviceName, rc);
    }

    /// <summary>
    /// Read the current <c>State</c> of a Win32_Service ("Running", "Stopped",
    /// "Start Pending", etc.). Returns null when the service is not installed.
    /// Used by AzureDataSource's PA-readiness gate to detect when WinGA has
    /// transitioned to Running, signalling PA has finished its OOBE chain.
    /// </summary>
    public static string? GetState(string serviceName)
    {
        using var session = CimSession.Create(null);
        using var instance = LookupService(session, serviceName);
        return instance is null ? null : GetString(instance, "State");
    }

    public static void StopService(string serviceName, ILogger logger)
    {
        using var session = CimSession.Create(null);
        using var instance = LookupService(session, serviceName)
            ?? throw new InvalidOperationException($"Service '{serviceName}' not found.");

        var state = GetString(instance, "State") ?? string.Empty;
        if (string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "Stop Pending", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Service {Name} already in state {State}; no-op.", serviceName, state);
            return;
        }

        using var result = session.InvokeMethod(instance, "StopService", new CimMethodParametersCollection());
        var rc = ConvertToUInt32(result.ReturnValue.Value);
        // 0 = success, 5 = service cannot accept control at this time. We don't
        // throw on 5 because the service might be in the middle of stopping
        // already — w32time disables itself rapidly on its trigger transitions.
        if (rc != 0 && rc != 5)
            throw new InvalidOperationException(
                $"Win32_Service.StopService({serviceName}) returned {rc}.");

        logger.LogDebug("Service {Name} StopService rc={Rc}.", serviceName, rc);
    }

    private static CimInstance? LookupService(CimSession session, string serviceName)
    {
        // WQL filter by Name keeps this a single round trip and avoids
        // materialising every service on the box.
        var escaped = serviceName.Replace("'", "''");
        var query = $"SELECT * FROM Win32_Service WHERE Name = '{escaped}'";
        foreach (var instance in session.QueryInstances(CimNamespace, "WQL", query))
        {
            return instance;
        }
        return null;
    }

    private static uint ConvertToUInt32(object? value) => value switch
    {
        uint u => u,
        int i => (uint)i,
        ushort s => s,
        _ => 0u,
    };

    private static string? GetString(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value as string;
}
