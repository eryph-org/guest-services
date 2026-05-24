namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record UserConfig
{
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "User name")]
    public string? Name { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Pre-hashed password (or plain-text on cloud-init <22 if PasswordHashing is disabled)")]
    public string? Passwd { get; init; }

    // Cloud-init alias for Passwd with explicit "store plaintext, no hashing" semantics; Passwd may be a hash.
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Plain-text password (cloud-init treats Passwd as ambiguous; PlainTextPasswd is explicit)")]
    public string? PlainTextPasswd { get; init; }

    /// <summary>
    /// Pre-hashed password (cloud-init explicit alias for <see cref="Passwd"/>
    /// when the value is known to be a crypt-format hash). Linux-only —
    /// Windows cannot apply a Linux crypt hash to a local account.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Pre-hashed password (Linux crypt hash; no Windows analogue)")]
    public string? HashedPasswd { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Lock the account password (cannot log in via password)")]
    public bool? LockPasswd { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Groups the user is added to")]
    public IReadOnlyList<string>? Groups { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "SSH authorized keys for the user")]
    public IReadOnlyList<string>? SshAuthorizedKeys { get; init; }

    /// <summary>
    /// Cloud-init's <c>inactive</c> accepts bool / int (days until lock) /
    /// string. We widen to <see cref="string"/> as the most-permissive form —
    /// the runtime application decides based on content. Linux-only; Windows
    /// has no direct analogue to <c>useradd --inactive</c>.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Days until account is locked after password expiry (Linux useradd --inactive)")]
    public string? Inactive { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Login shell")]
    public string? Shell { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Home directory path")]
    public string? HomeDir { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Primary group")]
    public string? PrimaryGroup { get; init; }

    /// <summary>
    /// Sudoers entry / entries. Cloud-init accepts either a single string or
    /// a list of strings — the latter produces one sudoers line per entry.
    /// On Windows this maps to "is the user a member of Administrators" — the
    /// list shape is honored at the schema level so cross-cloud cloud-config
    /// round-trips; Windows treats any non-"false" entry as the truthy signal.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Sudoers entries (list-form; cloud-init also accepts a single string)")]
    public IReadOnlyList<string>? Sudo { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "Create the user as a system account")]
    public bool? System { get; init; }

    /// <summary>
    /// GECOS field (full name + comment). Cross-platform: on Linux this
    /// populates <c>/etc/passwd</c>'s GECOS column; on Windows the runtime
    /// (Phase 3) maps it to the NTUser <c>FullName</c> property.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.All, Description = "GECOS field — full name / comment (Windows: NTUser FullName)")]
    public string? Gecos { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Import SSH keys from a remote source (e.g. Launchpad lp:user, GitHub gh:user)")]
    public IReadOnlyList<string>? SshImportId { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Redirect SSH logins to a default user with an error message")]
    public bool? SshRedirectUser { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Account expiry date in YYYY-MM-DD format")]
    public string? Expiredate { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Do not create the home directory")]
    public bool? NoCreateHome { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Do not create a user-private group")]
    public bool? NoUserGroup { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Do not initialize lastlog and faillog for the user")]
    public bool? NoLogInit { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "SELinux user mapping")]
    public string? SelinuxUser { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Numeric user ID")]
    public int? Uid { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Snap-specific user — assertions or email — see cloud-init snap_config")]
    public string? Snapuser { get; init; }
}
