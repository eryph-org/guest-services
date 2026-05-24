using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Direct tests for <see cref="WindowsOs.BuildLocaleScript"/> — the
/// PowerShell-string-building composition root. Without these, the script
/// is only exercised end-to-end via integration; a stray apostrophe in
/// an exotic locale or a wrong cmdlet name would surface only at runtime
/// on a real guest.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BuildLocaleScriptTests
{
    [Fact]
    public void Locale_only_emits_culture_ui_and_system_locale_cmdlets()
    {
        var script = WindowsOs.BuildLocaleScript(new LocaleSpec { Locale = "de-DE" });

        // Each cmdlet must appear with the requested culture, single-quoted.
        script.Should().Contain("Set-Culture -CultureInfo 'de-DE'");
        script.Should().Contain("Set-WinUILanguageOverride -Language 'de-DE'");
        script.Should().Contain("New-WinUserLanguageList -Language 'de-DE'");
        script.Should().Contain("Set-WinUserLanguageList -LanguageList $langList -Force");
        // Set-WinSystemLocale must be guarded by a current-state check so a
        // no-op run doesn't claim RebootRequired needlessly.
        script.Should().Contain("Get-WinSystemLocale");
        script.Should().Contain("Set-WinSystemLocale -SystemLocale 'de-DE'");
        // The REBOOT_REQUIRED marker line is what ApplyLocaleAsync parses.
        script.Should().Contain("REBOOT_REQUIRED=");
    }

    [Fact]
    public void Locale_and_keyboard_clears_existing_input_method_tips_first()
    {
        // Without the Clear() call the new language list would start with the
        // OS-default keyboard for the requested locale, then ALSO append our
        // requested layout — operators end up with two input methods where
        // they wanted one. Pin the Clear-then-Add order.
        var script = WindowsOs.BuildLocaleScript(new LocaleSpec
        {
            Locale = "de-DE",
            KeyboardLayout = "0407:00000407",
        });

        var clearIndex = script.IndexOf("$langList[0].InputMethodTips.Clear()", StringComparison.Ordinal);
        var addIndex = script.IndexOf("$langList[0].InputMethodTips.Add('0407:00000407')", StringComparison.Ordinal);
        clearIndex.Should().BeGreaterThan(-1);
        addIndex.Should().BeGreaterThan(clearIndex);
    }

    [Fact]
    public void Keyboard_only_amends_existing_user_language_list()
    {
        // When only the keyboard is changing we MUST NOT touch culture / UI
        // language / system locale. The amend path uses Get-WinUserLanguageList
        // (not New-) so the operator's existing language stack is preserved.
        var script = WindowsOs.BuildLocaleScript(new LocaleSpec { KeyboardLayout = "0409:00000409" });

        script.Should().Contain("Get-WinUserLanguageList");
        script.Should().NotContain("Set-Culture");
        script.Should().NotContain("Set-WinUILanguageOverride");
        script.Should().NotContain("Set-WinSystemLocale");
        script.Should().NotContain("New-WinUserLanguageList");
        script.Should().Contain("$langList[0].InputMethodTips.Add('0409:00000409')");
    }

    [Fact]
    public void Apostrophes_in_locale_are_doubled_for_single_quoted_powershell_strings()
    {
        // PowerShell single-quoted strings escape an apostrophe by doubling
        // it. A naive escape would break the script. Use a contrived locale
        // string to confirm the escape function is wired in.
        var script = WindowsOs.BuildLocaleScript(new LocaleSpec { Locale = "weird's-Locale" });

        script.Should().Contain("Set-Culture -CultureInfo 'weird''s-Locale'");
        // The unescaped form must not leak through.
        script.Should().NotContain("Set-Culture -CultureInfo 'weird's-Locale'");
    }

    [Fact]
    public void Empty_spec_produces_a_script_with_only_the_reboot_marker()
    {
        // Defensive shape: the OS layer should never be invoked with an
        // empty LocaleSpec (the module guards against this), but if it is
        // the generated script must still be safe to execute — it should
        // emit only the REBOOT_REQUIRED marker so the caller's stdout
        // parser yields RebootRequired=false.
        var script = WindowsOs.BuildLocaleScript(new LocaleSpec());

        script.Should().Contain("REBOOT_REQUIRED=$rebootRequired");
        script.Should().NotContain("Set-Culture");
        script.Should().NotContain("Set-WinUserLanguageList");
    }

    [Fact]
    public void Script_uses_stop_on_error_so_a_cmdlet_failure_is_terminating()
    {
        // ErrorActionPreference=Stop makes any cmdlet failure terminating;
        // ApplyLocaleAsync detects the non-zero exit and surfaces it as
        // ModuleOutcome.Failed. Without this, a half-applied locale could
        // silently succeed at the .exe level.
        var script = WindowsOs.BuildLocaleScript(new LocaleSpec { Locale = "en-US" });
        script.Should().Contain("$ErrorActionPreference = 'Stop'");
    }
}
