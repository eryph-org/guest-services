using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Eryph.GuestServices.Core;

/// <summary>
/// The operator-controllable guest-services capability switches, as a typed
/// enum so the provisioning agent can <em>write</em> the same flags
/// <see cref="IServiceControlFlags"/> reads. Map an entry to its persisted
/// value name with <see cref="PlatformServiceControlFlags.GetValueName"/>.
/// </summary>
public enum ServiceControlFlag
{
    /// <summary>The first-boot provisioning agent (<c>ProvisioningEnabled</c>).</summary>
    Provisioning,

    /// <summary>The remote-access transport (<c>RemoteAccessEnabled</c>).</summary>
    RemoteAccess,

    /// <summary>Honoring of KVP-delivered client keys (<c>KvpAuthEnabled</c>).</summary>
    KvpAuth,
}

/// <summary>
/// Operator on/off switches for the top-level guest-services capabilities.
/// All flags are <b>opt-out</b>: a capability is ON unless an operator has
/// explicitly turned it off. This is an injectable seam so the gated services
/// (provisioning agent, remote-access transport, KVP-auth honoring) can be
/// unit-tested without touching the real config sources.
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

    /// <summary>
    /// Gates whether authorized client keys delivered via Hyper-V data exchange
    /// (KVP) are honored. When <c>false</c>, only the locally provisioned
    /// (on-disk) key authorizes — a hardening option for environments that
    /// want eryph-zero / geneset to be the sole authority over guest access
    /// and where keys pushed at runtime via <c>egs-tool add-ssh-config</c>
    /// should be rejected. <c>true</c> unless an operator turned it off.
    /// </summary>
    bool IsKvpAuthEnabled();
}

/// <summary>
/// Default <see cref="IServiceControlFlags"/> implementation. Reads opt-out
/// flags from the platform-native config source:
/// <list type="bullet">
/// <item><description>Windows: <c>HKLM\SOFTWARE\eryph\guest-services</c>
/// REG_DWORD values (<c>0</c> = off, anything else = on).</description></item>
/// <item><description>Linux: <c>/etc/opt/eryph/guest-services/service-control.conf</c>
/// in <c>KEY=VALUE</c> format (<c>0</c> / <c>false</c> = off, anything
/// else = on). Blank lines and <c>#</c> comments are ignored.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// These are <b>fail-open</b> flags: a missing key/value, missing file, or any
/// I/O / permission error yields <c>true</c>. We never disable a capability
/// because of a read error — only an explicit <c>0</c> (or <c>false</c> on
/// Linux) turns a capability off. The value→bool interpretation lives in two
/// pure helpers — <see cref="InterpretWindowsRegistryValue"/> for the
/// REG_DWORD path and <see cref="InterpretLinuxConfigValue"/> for the
/// KEY=VALUE path — kept separate so a mistyped REG_SZ on Windows cannot
/// accidentally exercise the Linux string-parsing rules. Platform-gated reads
/// are split behind <c>[SupportedOSPlatform]</c> attributes plus an
/// <see cref="OperatingSystem"/> gate so CA1416 stays clean.
/// </remarks>
public sealed class PlatformServiceControlFlags : IServiceControlFlags
{
    /// <summary>
    /// Windows registry key (under HKLM) holding the opt-out flags. Public so a
    /// writer (the provisioning agent) targets the exact key this reader uses.
    /// </summary>
    public const string WindowsServiceControlKey = @"SOFTWARE\eryph\guest-services";
    internal const string LinuxServiceControlConfigPath = "/etc/opt/eryph/guest-services/service-control.conf";

    internal const string ProvisioningEnabledValue = "ProvisioningEnabled";
    internal const string RemoteAccessEnabledValue = "RemoteAccessEnabled";
    internal const string KvpAuthEnabledValue = "KvpAuthEnabled";

    /// <summary>
    /// Persisted value name for a flag — the REG_DWORD value name on Windows
    /// and the <c>KEY=</c> name on Linux are identical. Shared by the reader
    /// here and any writer so the two never drift.
    /// </summary>
    public static string GetValueName(ServiceControlFlag flag) => flag switch
    {
        ServiceControlFlag.Provisioning => ProvisioningEnabledValue,
        ServiceControlFlag.RemoteAccess => RemoteAccessEnabledValue,
        ServiceControlFlag.KvpAuth => KvpAuthEnabledValue,
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Unknown service-control flag."),
    };

    public bool IsProvisioningEnabled() => ReadFlag(ProvisioningEnabledValue);

    public bool IsRemoteAccessEnabled() => ReadFlag(RemoteAccessEnabledValue);

    public bool IsKvpAuthEnabled() => ReadFlag(KvpAuthEnabledValue);

    /// <summary>
    /// Pure value→bool interpretation for a Windows REG_DWORD opt-out flag.
    /// Off iff the registry value is the integer <c>0</c>. Anything else —
    /// <see langword="null"/>, a REG_SZ string (even <c>"0"</c>), an
    /// unrecognised kind — yields <c>true</c> (default ON / fail-open).
    /// The documented Windows control surface is REG_DWORD only; honouring
    /// strings would silently change long-standing behaviour.
    /// </summary>
    internal static bool InterpretWindowsRegistryValue(object? regValue) =>
        regValue is int i ? i != 0 : true;

    /// <summary>
    /// Pure value→bool interpretation for a Linux config-file opt-out flag.
    /// Off iff the value is the literal string <c>"0"</c> or <c>"false"</c>
    /// (case-insensitive, whitespace tolerated). Anything else — including
    /// <see langword="null"/>, empty, or unparseable strings — yields
    /// <c>true</c> (default ON / fail-open).
    /// </summary>
    internal static bool InterpretLinuxConfigValue(string? rawValue) => rawValue switch
    {
        null => true,
        var s when int.TryParse(s.Trim(), out var n) => n != 0,
        var s when bool.TryParse(s.Trim(), out var b) => b,
        _ => true,
    };

    private static bool ReadFlag(string valueName)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ReadWindowsFlag(valueName);
            if (OperatingSystem.IsLinux())
                return ReadLinuxFlag(valueName);
            return true;
        }
        catch
        {
            // Fail-open: any I/O / permission error must never disable a
            // capability. These are opt-out flags; only an explicit "0" /
            // "false" turns them off.
            return true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadWindowsFlag(string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(WindowsServiceControlKey);
        return InterpretWindowsRegistryValue(key?.GetValue(valueName));
    }

    [SupportedOSPlatform("linux")]
    private static bool ReadLinuxFlag(string valueName)
    {
        if (!File.Exists(LinuxServiceControlConfigPath))
            return true;

        foreach (var line in File.ReadAllLines(LinuxServiceControlConfigPath))
        {
            var entry = ParseConfigLine(line);
            if (entry is null)
                continue;
            if (!string.Equals(entry.Value.key, valueName, StringComparison.OrdinalIgnoreCase))
                continue;
            return InterpretLinuxConfigValue(entry.Value.value);
        }

        return true;
    }

    /// <summary>
    /// Pure parser for a single line of the Linux service-control.conf file.
    /// Returns <see langword="null"/> for blank lines and <c>#</c> comments,
    /// or a <c>(key, value)</c> tuple for a well-formed <c>KEY=VALUE</c>
    /// line. Whitespace around the key and value is trimmed.
    /// </summary>
    internal static (string key, string value)? ParseConfigLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return null;

        var eq = trimmed.IndexOf('=');
        if (eq <= 0)
            return null;

        return (trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim());
    }
}
