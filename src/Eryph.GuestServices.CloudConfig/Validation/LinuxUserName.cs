using System.Text.RegularExpressions;
using Dbosoft.Functional.DataTypes;
using LanguageExt;
using LanguageExt.ClassInstances;
using LanguageExt.Common;

using static LanguageExt.Prelude;

namespace Eryph.GuestServices.CloudConfig.Validation;

public class LinuxUserName : ValidatingNewType<LinuxUserName, string, OrdStringOrdinal>
{
    private static readonly Regex Pattern = new(
        @"^[a-z_][a-z0-9_-]*\$?$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public LinuxUserName(string value) : base(value ?? string.Empty)
    {
        ValidOrThrow(Validate(value));
    }

    private static Validation<Error, Unit> Validate(string? value) =>
        from nonEmpty in Eryph.ConfigModel.Validations.ValidateNotEmpty(value, "Linux user name")
        from _ in Eryph.ConfigModel.Validations.ValidateLength(nonEmpty, "Linux user name", 1, 32)
        from __ in guard(Pattern.IsMatch(nonEmpty),
                Error.New("The Linux user name must start with a lowercase letter or underscore "
                    + "and may contain only lowercase letters, digits, dashes and underscores."))
            .ToValidation()
        select unit;
}
