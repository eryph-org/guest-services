using System.Text.RegularExpressions;
using LanguageExt;
using LanguageExt.Common;
using EryphValidations = Eryph.ConfigModel.Validations;

using static LanguageExt.Prelude;

namespace Eryph.GuestServices.CloudConfig.Validation;

public static class CloudConfigValidations
{
    private static readonly Regex HostnameLabelRegex = new(
        @"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly System.Collections.Generic.HashSet<string> ValidWriteFileEncodings = new(StringComparer.OrdinalIgnoreCase)
    {
        "b64", "base64",
        "gz", "gzip",
        "gz+b64", "gzip+b64", "gz+base64", "gzip+base64",
    };

    // RANDOM is intentionally absent: random password generation is not
    // supported on Windows guests (no out-of-band channel to return the
    // generated password — cloud-init relies on /dev/console, which has no
    // reliable Windows analogue across the clouds eryph targets). `type: RANDOM`
    // gets a tailored rejection below; a password-less entry (which cloud-init
    // also treats as RANDOM) is rejected too.
    private static readonly System.Collections.Generic.HashSet<string> ValidChpasswdTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "hash",
    };

    public static Validation<Error, CloudConfig> ValidateCloudConfig(CloudConfig config) =>
        ((config.Hostname is null
                ? Success<Error, Unit>(unit)
                : ValidateHostname(config.Hostname, "hostname"))
            | (config.Fqdn is null
                ? Success<Error, Unit>(unit)
                : ValidateFqdn(config.Fqdn))
            | ValidateUserList(config.Users)
            | ValidateGroupList(config.Groups)
            | (config.Chpasswd is null
                ? Success<Error, Unit>(unit)
                : ValidateChpasswdConfig(config.Chpasswd).Map(_ => unit))
            | ValidateWriteFileList(config.WriteFiles)
            | ValidateRuncmdList(config.Runcmd)
            | (config.Growpart is null
                ? Success<Error, Unit>(unit)
                : Prefix("growpart", GrowpartGrammar.Validate(config.Growpart)))
            | (config.PowerState is null
                ? Success<Error, Unit>(unit)
                : Prefix("power_state", PowerStateGrammar.Validate(config.PowerState))))
        .Map(_ => config);

    public static Validation<Error, UserConfig> ValidateUserConfig(UserConfig user, int index) =>
        Prefix($"users[{index}]",
            ((from name in EryphValidations.ValidateNotEmpty(user.Name, "name")
              from _ in ValidateUserName(name)
              select unit)
                | (user.PrimaryGroup is null
                    ? Success<Error, Unit>(unit)
                    : ValidateGroupName(user.PrimaryGroup, "primary_group"))
                | ValidateMembersList(user.Groups, "groups", ValidateGroupName)
                | guardnot(user.Passwd is not null && user.PlainTextPasswd is not null,
                        Error.New("Cannot specify both 'passwd' and 'plain_text_passwd' for the same user."))
                    .ToValidation())
            .Map(_ => user));

    public static Validation<Error, GroupConfig> ValidateGroupConfig(GroupConfig groupConfig, int index) =>
        Prefix($"groups[{index}]",
            ((from name in EryphValidations.ValidateNotEmpty(groupConfig.Name, "name")
              from _ in ValidateGroupName(name, "name")
              select unit)
                | ValidateMembersList(groupConfig.Members, "members", (n, _) => ValidateUserName(n)))
            .Map(_ => groupConfig));

    public static Validation<Error, ChpasswdConfig> ValidateChpasswdConfig(ChpasswdConfig chpasswd) =>
        Prefix("chpasswd",
            from _ in guardnot(chpasswd.Users is { Count: > 0 } && !string.IsNullOrEmpty(chpasswd.List),
                    Error.New("Cannot specify both 'users' and 'list' for chpasswd."))
                .ToValidation()
            from __ in chpasswd.Users is null
                ? Success<Error, Unit>(unit)
                : chpasswd.Users
                    .ToSeq()
                    .Map((i, e) => ValidateChpasswdListEntry(e, i).Map(_ => unit))
                    .Sequence()
                    .Map(_ => unit)
            select chpasswd);

    public static Validation<Error, ChpasswdListEntry> ValidateChpasswdListEntry(ChpasswdListEntry entry, int index) =>
        Prefix($"users[{index}]",
            ((from name in EryphValidations.ValidateNotEmpty(entry.Name, "name")
              from _ in ValidateUserName(name)
              select unit)
                | guardnot(
                        string.Equals(entry.Type, "RANDOM", StringComparison.OrdinalIgnoreCase)
                        || (entry.Type is null && string.IsNullOrEmpty(entry.Password)),
                        Error.New("Random password generation is not supported on Windows guests: "
                            + "there is no out-of-band channel to return the generated password. "
                            + "Specify an explicit password (type: text or hash)."))
                    .ToValidation()
                | (entry.Type is null
                    ? Success<Error, Unit>(unit)
                    : guard(ValidChpasswdTypes.Contains(entry.Type),
                            Error.New($"The chpasswd type '{entry.Type}' is invalid. "
                                + "Valid values are: text, hash."))
                        .ToValidation()))
            .Map(_ => entry));

    public static Validation<Error, WriteFileConfig> ValidateWriteFileConfig(WriteFileConfig file, int index) =>
        Prefix($"write_files[{index}]",
            ((from path in EryphValidations.ValidateNotEmpty(file.Path, "path")
              from _ in UnixFilePath.NewValidation(path).Map(_ => unit)
              select unit)
                | (file.Permissions is null
                    ? Success<Error, Unit>(unit)
                    : WriteFilePermissions.NewValidation(file.Permissions).Map(_ => unit))
                | (file.Encoding is null
                    ? Success<Error, Unit>(unit)
                    : guard(ValidWriteFileEncodings.Contains(file.Encoding),
                            Error.New($"The encoding '{file.Encoding}' is not supported. "
                                + "Valid values are: b64, base64, gz, gzip, gz+b64, gzip+b64, gz+base64, gzip+base64."))
                        .ToValidation()))
            .Map(_ => file));

    public static Validation<Error, RuncmdEntry> ValidateRuncmdEntry(RuncmdEntry entry, int index) =>
        Prefix($"runcmd[{index}]",
            from _ in entry.IsShellCommand
                ? ValidateShellRuncmd(entry)
                : ValidateExecRuncmd(entry)
            select entry);

    public static Validation<Error, Unit> ValidateHostname(string? value, string name) =>
        from nonEmpty in EryphValidations.ValidateNotEmpty(value, name)
        from _ in EryphValidations.ValidateLength(nonEmpty, name, 1, 63)
        from __ in guard(HostnameLabelRegex.IsMatch(nonEmpty),
                Error.New($"The {name} '{nonEmpty}' is not a valid hostname label. "
                    + "Allowed characters are letters, digits and hyphens; the label must not start or end with a hyphen."))
            .ToValidation()
        select unit;

    public static Validation<Error, Unit> ValidateFqdn(string value) =>
        from nonEmpty in EryphValidations.ValidateNotEmpty(value, "fqdn")
        from _ in EryphValidations.ValidateLength(nonEmpty, "fqdn", 1, 253)
        from ___ in guard(nonEmpty.Contains('.'),
                Error.New($"The fqdn '{nonEmpty}' must be fully qualified and contain at least one dot."))
            .ToValidation()
        from __ in nonEmpty.Split('.').ToSeq()
            .Map((i, label) => ValidateHostname(label, $"fqdn label[{i}]"))
            .Sequence()
            .Map(_ => unit)
        select unit;

    private static Validation<Error, Unit> ValidateShellRuncmd(RuncmdEntry entry) =>
        from _ in EryphValidations.ValidateNotEmpty(entry.Command, "command")
        from __ in guard(entry.Argv is null,
                Error.New("A shell-style runcmd entry must not provide both 'command' and 'argv'."))
            .ToValidation()
        select unit;

    private static Validation<Error, Unit> ValidateExecRuncmd(RuncmdEntry entry) =>
        from _ in guard(entry.Argv is { Count: > 0 },
                Error.New("An exec-style runcmd entry must provide a non-empty 'argv' list."))
            .ToValidation()
        from __ in guard(entry.Command is null,
                Error.New("An exec-style runcmd entry must not provide both 'command' and 'argv'."))
            .ToValidation()
        from ___ in entry.Argv!.ToSeq()
            .Map((i, a) => EryphValidations.ValidateNotEmpty(a, $"argv[{i}]").Map(_ => unit))
            .Sequence()
            .Map(_ => unit)
        select unit;

    private static Validation<Error, Unit> ValidateUserName(string name) =>
        WindowsUserName.NewValidation(name).Map(_ => unit)
            .Match(
                Succ: _ => Success<Error, Unit>(unit),
                Fail: windowsErrors => LinuxUserName.NewValidation(name).Map(_ => unit)
                    .Match(
                        Succ: _ => Success<Error, Unit>(unit),
                        Fail: linuxErrors => Fail<Error, Unit>(Error.New(
                            $"The user name '{name}' is not a valid Windows or Linux user name. "
                            + "It must satisfy at least one of the platform conventions.",
                            Error.Many(windowsErrors.Concat(linuxErrors).ToArray())))));

    private static Validation<Error, Unit> ValidateGroupName(string name, string field) =>
        from _ in EryphValidations.ValidateNotEmpty(name, field)
        from __ in EryphValidations.ValidateLength(name, field, 1, 64)
        select unit;

    private static Validation<Error, Unit> ValidateMembersList(
        IReadOnlyList<string>? names,
        string field,
        Func<string, string, Validation<Error, Unit>> validator) =>
        names is null
            ? Success<Error, Unit>(unit)
            : names.ToSeq()
                .Map((i, n) => Prefix($"{field}[{i}]",
                    from nonEmpty in EryphValidations.ValidateNotEmpty(n, field)
                    from _ in validator(nonEmpty, field)
                    select unit))
                .Sequence()
                .Map(_ => unit);

    private static Validation<Error, Unit> ValidateUserList(IReadOnlyList<UserConfig>? users) =>
        users is null
            ? Success<Error, Unit>(unit)
            : (users.ToSeq().Map((i, u) => ValidateUserConfig(u, i).Map(_ => unit)).Sequence().Map(_ => unit)
                | EryphValidations.ValidateDistinct(
                    users.Where(u => !string.IsNullOrEmpty(u.Name)),
                    u => Success<Error, string>(u.Name!.ToLowerInvariant()),
                    "user name"));

    private static Validation<Error, Unit> ValidateGroupList(IReadOnlyList<GroupConfig>? groups) =>
        groups is null
            ? Success<Error, Unit>(unit)
            : (groups.ToSeq().Map((i, g) => ValidateGroupConfig(g, i).Map(_ => unit)).Sequence().Map(_ => unit)
                | EryphValidations.ValidateDistinct(
                    groups.Where(g => !string.IsNullOrEmpty(g.Name)),
                    g => Success<Error, string>(g.Name!.ToLowerInvariant()),
                    "group name"));

    private static Validation<Error, Unit> ValidateWriteFileList(IReadOnlyList<WriteFileConfig>? files) =>
        files is null
            ? Success<Error, Unit>(unit)
            : files.ToSeq()
                .Map((i, f) => ValidateWriteFileConfig(f, i).Map(_ => unit))
                .Sequence()
                .Map(_ => unit);

    private static Validation<Error, Unit> ValidateRuncmdList(IReadOnlyList<RuncmdEntry>? entries) =>
        entries is null
            ? Success<Error, Unit>(unit)
            : entries.ToSeq()
                .Map((i, e) => ValidateRuncmdEntry(e, i).Map(_ => unit))
                .Sequence()
                .Map(_ => unit);

    private static Validation<Error, T> Prefix<T>(string prefix, Validation<Error, T> validation) =>
        validation.MapFail(err => Error.New($"{prefix}: {err.Message}", err));
}
