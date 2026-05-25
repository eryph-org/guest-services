using Dbosoft.Functional.DataTypes;
using Eryph.ConfigModel;
using LanguageExt;
using LanguageExt.ClassInstances;
using LanguageExt.Common;

using static LanguageExt.Prelude;

namespace Eryph.GuestServices.CloudConfig.Validation;

public class WindowsUserName : ValidatingNewType<WindowsUserName, string, OrdStringOrdinalIgnoreCase>
{
    public WindowsUserName(string value) : base(Normalize(value))
    {
        ValidOrThrow(Validate(Normalize(value)));
    }

    private static string Normalize(string? value) => value?.ToLowerInvariant() ?? string.Empty;

    private static readonly char[] InvalidCharacters =
    [
        '"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>'
    ];

    private static Validation<Error, Unit> Validate(string? value) =>
        from nonEmpty in Validations.ValidateNotEmpty(value, "Windows user name")
        from _ in Validations.ValidateLength(nonEmpty, "Windows user name", 1, 20)
        from __ in guardnot(nonEmpty.Any(c => InvalidCharacters.Contains(c) || char.IsControl(c)),
                Error.New("The Windows user name contains invalid characters. "
                    + "The following characters are not permitted: \" / \\ [ ] : ; | = , + * ? < > or control characters."))
            .ToValidation()
        from ___ in guardnot(nonEmpty.All(c => c == '.' || c == ' '),
                Error.New("The Windows user name cannot consist only of dots and spaces."))
            .ToValidation()
        select unit;
}
