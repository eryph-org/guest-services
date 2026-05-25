namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Desired locale / keyboard state passed to
/// <see cref="IWindowsOs.ApplyLocaleAsync"/>.
/// </summary>
public sealed record LocaleSpec
{
    /// <summary>
    /// BCP-47 culture name (e.g. <c>en-US</c>, <c>de-DE</c>). Applied to
    /// Set-Culture / Set-WinUILanguageOverride / Set-WinSystemLocale. Null
    /// leaves the existing values in place.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>
    /// Keyboard input method as a BCP-47 culture or a <c>language:klid</c>
    /// pair (e.g. <c>0409:00000409</c>). Null lets the platform pick the
    /// default keyboard for <see cref="Locale"/>.
    /// </summary>
    public string? KeyboardLayout { get; init; }
}

/// <summary>
/// Outcome of an <see cref="IWindowsOs.ApplyLocaleAsync"/> call. The
/// <c>RebootRequired</c> bit is the only signal the module needs — it
/// triggers <see cref="Modules.ModuleOutcome.RebootRequested"/>.
/// </summary>
public sealed record LocaleApplyResult
{
    /// <summary>
    /// True when the system locale (Win32 ANSI codepage) was changed and
    /// Windows needs a reboot for non-Unicode programs to pick it up.
    /// </summary>
    public bool RebootRequired { get; init; }
}
