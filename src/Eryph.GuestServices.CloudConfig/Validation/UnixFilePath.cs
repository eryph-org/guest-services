using Dbosoft.Functional.DataTypes;
using LanguageExt;
using LanguageExt.ClassInstances;
using LanguageExt.Common;

using static LanguageExt.Prelude;

namespace Eryph.GuestServices.CloudConfig.Validation;

public class UnixFilePath : ValidatingNewType<UnixFilePath, string, OrdStringOrdinal>
{
    private static readonly char[] InvalidCharacters = ['\0', '\\'];

    public UnixFilePath(string value) : base(value ?? string.Empty)
    {
        ValidOrThrow(Validate(value));
    }

    private static Validation<Error, Unit> Validate(string? value) =>
        from nonEmpty in Eryph.ConfigModel.Validations.ValidateNotEmpty(value, "file path")
        from _ in guard(nonEmpty.StartsWith('/'),
                Error.New("The file path must be an absolute Unix path starting with '/'."))
            .ToValidation()
        from __ in guardnot(nonEmpty.Any(c => InvalidCharacters.Contains(c) || char.IsControl(c)),
                Error.New("The file path contains invalid characters."))
            .ToValidation()
        from ___ in guardnot(ContainsRelativeSegment(nonEmpty),
                Error.New("The file path must not contain relative segments ('.' or '..')."))
            .ToValidation()
        select unit;

    private static bool ContainsRelativeSegment(string path) =>
        path.Split('/').Any(segment => segment is "." or "..");
}
