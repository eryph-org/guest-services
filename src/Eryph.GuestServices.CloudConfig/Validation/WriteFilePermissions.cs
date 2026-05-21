using Dbosoft.Functional.DataTypes;
using LanguageExt;
using LanguageExt.ClassInstances;
using LanguageExt.Common;

using static LanguageExt.Prelude;

namespace Eryph.GuestServices.CloudConfig.Validation;

public class WriteFilePermissions : ValidatingNewType<WriteFilePermissions, string, OrdStringOrdinal>
{
    public WriteFilePermissions(string value) : base(Normalize(value))
    {
        ValidOrThrow(Validate(value));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var digits = StripPrefix(value);
        return IsOctal(digits) && digits.Length <= 4
            ? digits.PadLeft(4, '0')
            : value;
    }

    private static string StripPrefix(string value) =>
        value.StartsWith("0o", StringComparison.Ordinal) || value.StartsWith("0O", StringComparison.Ordinal)
            ? value[2..]
            : value;

    private static bool IsOctal(string digits) =>
        digits.Length > 0 && digits.All(c => c is >= '0' and <= '7');

    private static Validation<Error, Unit> Validate(string? value) =>
        from nonEmpty in Eryph.ConfigModel.Validations.ValidateNotEmpty(value, "file permissions")
        let digits = StripPrefix(nonEmpty)
        from _ in guard(IsOctal(digits),
                Error.New("The file permissions must be an octal number such as \"0644\"."))
            .ToValidation()
        from __ in guardnot(digits.Length > 4,
                Error.New("The file permissions must have at most four octal digits."))
            .ToValidation()
        select unit;
}
