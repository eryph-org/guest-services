namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_ansible</c> bootstrap configuration. Linux-only;
/// no-op on Windows today.
/// </summary>
[CloudInitRecord]
public sealed record AnsibleConfig
{
    /// <summary>Distro package name (defaults to <c>ansible</c>).</summary>
    public string? PackageName { get; init; }

    /// <summary>Install method — <c>distro</c> or <c>pip</c>.</summary>
    public string? InstallMethod { get; init; }

    /// <summary>
    /// Path to a custom <c>ansible.cfg</c>. The C# property is renamed
    /// because the YAML key (<c>ansible_config</c>) maps to a name that
    /// would clash with the enclosing record type. The YAML alias is wired
    /// externally via <c>WithAttributeOverride</c> so the model stays
    /// YamlDotNet-free.
    /// </summary>
    public string? AnsibleConfigPath { get; init; }

    /// <summary>Unix user account ansible runs as.</summary>
    public string? RunUser { get; init; }

    /// <summary>Optional controller-mode setup (repos / playbook orchestration).</summary>
    public AnsibleSetupController? SetupController { get; init; }

    /// <summary>Optional <c>ansible-galaxy</c> orchestration.</summary>
    public AnsibleGalaxy? Galaxy { get; init; }

    /// <summary>Optional <c>ansible-pull</c> orchestration.</summary>
    public AnsiblePull? Pull { get; init; }
}

/// <summary>
/// Cloud-init <c>cc_ansible</c> controller-mode block. Linux-only.
/// </summary>
[CloudInitRecord]
public sealed record AnsibleSetupController
{
    /// <summary>Git repositories to clone for controller orchestration. Opaque entries.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? Repositories { get; init; }

    /// <summary>Playbooks / commands the controller runs after setup. Opaque entries.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? RunAnsible { get; init; }
}

/// <summary>
/// Cloud-init <c>cc_ansible</c> <c>galaxy</c> orchestration block.
/// </summary>
[CloudInitRecord]
public sealed record AnsibleGalaxy
{
    /// <summary>
    /// Galaxy CLI invocations. Each entry is an argv-style command line
    /// passed to <c>ansible-galaxy</c>.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>>? Actions { get; init; }
}

/// <summary>
/// Cloud-init <c>cc_ansible</c> <c>pull</c> orchestration block.
/// </summary>
[CloudInitRecord]
public sealed record AnsiblePull
{
    /// <summary>Git repo URL the playbook lives in.</summary>
    public string? Url { get; init; }

    /// <summary>Playbook filename within the repo.</summary>
    public string? PlaybookName { get; init; }

    /// <summary>When true, ssh host keys are auto-accepted on first connection.</summary>
    public bool? AcceptHostKey { get; init; }

    /// <summary>When true, the local checkout is wiped before pulling.</summary>
    public bool? Clean { get; init; }

    /// <summary>When true, ansible-pull is invoked with the <c>--full</c> flag.</summary>
    public bool? Full { get; init; }

    /// <summary>Extra module search path.</summary>
    public string? ModulePath { get; init; }

    /// <summary>SSH private-key path for the git checkout.</summary>
    public string? PrivateKey { get; init; }

    /// <summary>When true, ansible-pull wipes intermediate state.</summary>
    public bool? Purge { get; init; }

    /// <summary>Random pre-run sleep window (e.g. <c>"3600"</c>).</summary>
    public string? Sleep { get; init; }

    /// <summary>Tag filter passed to ansible-pull.</summary>
    public string? Tags { get; init; }

    /// <summary>When set, ansible-pull tracks subscriptions on this branch.</summary>
    public string? TrackSubs { get; init; }

    /// <summary>Specific commit / branch to check out.</summary>
    public string? Checkout { get; init; }
}
