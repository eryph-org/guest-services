namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// Single source of truth for the 22 YAML 1.1 / PyYAML SafeLoader bool
/// tokens. Cloud-init reads cloud-config via <c>yaml.safe_load</c>, so
/// this is the set our scalar resolver and BoolOrString converter must
/// recognise to stay observably equivalent.
/// </summary>
internal static class Yaml11BoolTokens
{
    // Match is case-exact per the YAML 1.1 implicit type resolver — only
    // the listed casings are recognised (e.g. `yEs` is NOT a bool). PyYAML
    // SafeLoader's regex is the same:
    // ^(?:y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)$
    private static readonly Dictionary<string, bool> Tokens = new(StringComparer.Ordinal)
    {
        // true / True / TRUE
        ["true"] = true,
        ["True"] = true,
        ["TRUE"] = true,
        // false / False / FALSE
        ["false"] = false,
        ["False"] = false,
        ["FALSE"] = false,
        // yes / Yes / YES
        ["yes"] = true,
        ["Yes"] = true,
        ["YES"] = true,
        // no / No / NO
        ["no"] = false,
        ["No"] = false,
        ["NO"] = false,
        // on / On / ON
        ["on"] = true,
        ["On"] = true,
        ["ON"] = true,
        // off / Off / OFF
        ["off"] = false,
        ["Off"] = false,
        ["OFF"] = false,
        // y / Y
        ["y"] = true,
        ["Y"] = true,
        // n / N
        ["n"] = false,
        ["N"] = false,
    };

    /// <summary>
    /// Returns <c>true</c> and the resolved bool value when <paramref name="text"/>
    /// is one of the 22 YAML 1.1 implicit bool tokens. Match is exact-case;
    /// no trimming or normalisation is performed.
    /// </summary>
    public static bool TryParse(string text, out bool value) => Tokens.TryGetValue(text, out value);

    /// <summary>
    /// <c>true</c> when <paramref name="text"/> matches one of the YAML 1.1
    /// implicit bool tokens. Convenience for callers that don't need the value.
    /// </summary>
    public static bool IsBoolToken(string text) => Tokens.ContainsKey(text);
}
