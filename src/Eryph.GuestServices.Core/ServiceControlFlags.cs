using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Eryph.GuestServices.Core;

/// <summary>
/// Operator on/off switches for the two top-level guest-services capabilities.
/// Both are <b>opt-out</b>: a capability is ON unless an operator has explicitly
/// turned it off. This is an injectable seam so the gated services
/// (provisioning agent, remote-access transport) can be unit-tested without
/// touching the real registry.
/// </summary>
public interface IServiceControlFlags
{
    /// <summary>
    /// Gates the cloud-init-style first-boot provisioning agent. <c>true</c>
    /// unless an operator turned it off.
    /// </summary>
    bool IsProvisioningEnabled();

    /// <summary>
    /// Gates eryph's remote-access transport (the Hyper-V-vsock SSH server that
    /// <c>egs-tool</c> connects to for shell / exec / file-transfer / pty).
    /// <c>true</c> unless an operator turned it off.
    /// </summary>
    bool IsRemoteAccessEnabled();
}

/// <summary>
/// Default <see cref="IServiceControlFlags"/> implementation. Reads the opt-out
/// flags from <c>HKLM\SOFTWARE\eryph\guest-services</c> on Windows; on every
/// other platform both capabilities are always ON (the registry is
/// Windows-only).
/// </summary>
/// <remarks>
/// These are <b>fail-open</b> flags: a missing key/value or any registry /
/// permission error yields <c>true</c>. We never disable a capability because of
/// a read error — only an explicit <c>REG_DWORD</c> value of <c>0</c> turns a
/// capability off. The value→bool interpretation lives in the pure
/// <see cref="InterpretFlag"/> helper so it can be unit-tested without writing
/// to HKLM (which needs admin and pollutes the machine). The Windows-only
/// registry access is split behind a <c>[SupportedOSPlatform("windows")]</c>
/// core plus an <see cref="OperatingSystem.IsWindows"/> gate, mirroring
/// <c>PlatformProbes</c> / <c>AzureDataSource</c> so CA1416 stays clean.
/// </remarks>
public sealed class RegistryServiceControlFlags : IServiceControlFlags
{
    internal const string ServiceControlKey = @"SOFTWARE\eryph\guest-services";
    internal const string ProvisioningEnabledValue = "ProvisioningEnabled";
    internal const string RemoteAccessEnabledValue = "RemoteAccessEnabled";

    public bool IsProvisioningEnabled() => ReadFlag(ProvisioningEnabledValue);

    public bool IsRemoteAccessEnabled() => ReadFlag(RemoteAccessEnabledValue);

    /// <summary>
    /// Pure value→bool interpretation for an opt-out DWORD flag. A
    /// <c>REG_DWORD</c> reads back as <see cref="int"/>: <c>0</c> means OFF,
    /// any non-zero value means ON. A missing value (<c>null</c>) or any other
    /// value kind means ON. Kept pure (no registry I/O) so the opt-out
    /// semantics are unit-testable without admin or machine state.
    /// </summary>
    internal static bool InterpretFlag(object? regValue) =>
        regValue is int i ? i != 0 : true;

    private static bool ReadFlag(string valueName)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            return ReadFlagCore(valueName);
        }
        catch
        {
            // Fail-open: a registry / permission error must never disable a
            // capability. These are opt-out flags; only an explicit 0 turns
            // them off.
            return true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadFlagCore(string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceControlKey);
        return InterpretFlag(key?.GetValue(valueName));
    }
}
