# RFC 0018 — Windows OpenSSH daemon configuration (`SshModule`)

Status: Draft

## Problem

`SshAuthorizedKeysModule` today only writes per-user `authorized_keys`
files. Cloud-init's `cc_ssh` is much broader: it manages host keys
(generate fresh on first boot, or import operator-supplied), edits
`sshd_config` (`PasswordAuthentication`, `PermitRootLogin`), and surfaces
the host-key fingerprints via reporting. We promise "behaves like
cloud-init / cbi" but we silently drop most of that block today.

The egs-service Hyper-V-socket SSH transport is a separate server
(documented in [RFC 0008](0008-platform-native-provisioner-coexistence.md));
this module must not modify it. The OS-level `sshd` is a SECOND server,
and that's the one this RFC configures.

## What cloud-init does

`cc_ssh` covers four things
(<https://cloudinit.readthedocs.io/en/latest/reference/modules.html#ssh>):

1. **Host keys.** If `ssh_keys:` is present, write the supplied keypairs
   to `/etc/ssh/ssh_host_<type>_key{,.pub}`. Otherwise generate fresh
   keys on first boot (controlled by `ssh_genkeytypes`, defaulting to
   `[rsa, ecdsa, ed25519]`). `ssh_deletekeys: true` deletes any
   pre-existing keys before generation — the standard "stripped image,
   fresh identity per instance" path.
2. **Authorized keys.** `ssh_authorized_keys:` at top level + per-user
   `ssh_authorized_keys:`, merged into each user's `~/.ssh/authorized_keys`.
3. **sshd_config edits.** `ssh_pwauth: true|false|unchanged` toggles
   `PasswordAuthentication`. `disable_root: true` writes a
   `PermitRootLogin no` entry (or a `Match`-shaped block with the
   no-root-login command, depending on the distro).
4. **Reporting.** Emits the host-key fingerprints as a `ssh_keys`
   reporting event so the operator's console can show them.

Default frequency: per-instance.

## What cloudbase-init does

Cbi does not manage Win32-OpenSSH at all — its `setuserpassword.py`
ecosystem grew out of the WinRM-only era. The closest plugin is
`createuser.py` (just writes `authorized_keys`-shaped data into the
user's profile). See
<https://github.com/cloudbase/cloudbase-init/blob/master/cloudbaseinit/plugins/common/>.

This is again a place we extend past cbi: the gene corpus already
configures sshd via fodder scripts, and we want to absorb that work.

## What Windows needs

Win32-OpenSSH quirks that matter here:

- Admin users' keys live in `C:\ProgramData\ssh\administrators_authorized_keys`,
  not in `%USERPROFILE%\.ssh\authorized_keys`. Already handled by our
  existing `SetUserSshAuthorizedKeysAsync`.
- Config file is `C:\ProgramData\ssh\sshd_config` (not `/etc/ssh/`).
- Host keys live in `C:\ProgramData\ssh\ssh_host_<type>_key{,.pub}`.
  Generation tool is `ssh-keygen.exe`.
- Service name is `sshd`. `Restart-Service sshd` reloads config.
- ACLs on host keys must be tight or sshd refuses to start. The
  `FixHostFilePermissions.ps1` script ships with Win32-OpenSSH; we
  should call it (or replicate the ACL set) after writing host keys.

## Tentative direction

Rename `SshAuthorizedKeysModule` → `SshModule` and broaden it to cover
the same surface cloud-init does. The existing authorized-keys path is
preserved (it's still the most common case).

### POCO shape

Mirror `cc_ssh` so YAML round-trips. Top-level keys
(`ssh_authorized_keys`, `ssh_pwauth`, `disable_root`) keep their current
top-level position; the structured block goes under `ssh:`:

```csharp
public sealed record SshConfig
{
    public IReadOnlyList<SshHostKey>? SshKeys { get; init; }     // pre-supplied keys
    public IReadOnlyList<string>? SshGenkeytypes { get; init; }  // ["rsa", "ed25519"]
    public bool? SshDeletekeys { get; init; }
    public bool? SshQuietKeygen { get; init; }
}

public sealed record SshHostKey
{
    public string Type { get; init; } = "";   // rsa | ecdsa | ed25519
    public string? Private { get; init; }
    public string? Public { get; init; }
}
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.SshModule`
  (Stage = Config, Order = current `SshAuthorizedKeysModule` slot,
  Frequency = PerInstance).
- Behaviour, in order:
  1. If `ssh.ssh_deletekeys` (or no `ssh.ssh_keys` and the host has none),
     delete `C:\ProgramData\ssh\ssh_host_*_key{,.pub}`.
  2. If `ssh.ssh_keys` present, write each keypair verbatim and fix ACLs.
     Else, generate the types listed in `ssh.ssh_genkeytypes`
     (defaulting to `[rsa, ecdsa, ed25519]`).
  3. Edit `sshd_config`: `PasswordAuthentication`, `PermitRootLogin`.
     Edits are idempotent line-set operations (replace-or-append).
  4. Write per-user authorized keys (existing logic, unchanged).
  5. Restart sshd if step 1-3 changed anything. If sshd isn't installed,
     log Warning and continue.
- Report host-key fingerprints via `ReportingEvent.Progress` so an
  operator console can pick them up.

### IWindowsOs additions

```csharp
Task<bool> IsSshdInstalledAsync(CancellationToken ct);
Task WriteSshHostKeyAsync(string keyType, string privatePem, string publicLine, CancellationToken ct);
Task<IReadOnlyList<SshHostKeyFingerprint>> RegenerateSshHostKeysAsync(
    IReadOnlyList<string> keyTypes,
    bool deleteExisting,
    CancellationToken ct);
Task SetSshdConfigOptionAsync(string option, string value, CancellationToken ct);
Task RestartSshdAsync(CancellationToken ct);
// existing:
Task SetUserSshAuthorizedKeysAsync(string userName, IReadOnlyList<string> keys, CancellationToken ct);
```

`SetSshdConfigOptionAsync` is line-set semantics: find existing
`^\s*<option>\s+.*$`, replace; otherwise append. Mirrors what
cloud-init's `update_ssh_config` helper does.

### Don't break the egs-service transport

The egs-service SSH server (Hyper-V socket) is a separate process and
not influenced by `C:\ProgramData\ssh\sshd_config`. The `SshModule`
edits only the OS-level OpenSSH config; the egs-service transport
remains untouched.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — runcmd vs
  module ordering (sshd restart happens here, not in operator scripts).
- [RFC 0008](0008-platform-native-provisioner-coexistence.md) — the
  egs-service SSH transport is separate from OS-level OpenSSH.
- [RFC 0009](0009-module-list-split.md) — operators who manage sshd
  themselves disable via `disabledModules: [SshModule]`.

## Open questions

- Do we manage `sshd` service start-type (Automatic)? Tentative no —
  leave it to the gene baseline so this module remains config-only.
- `ssh_pwauth: unchanged` (cloud-init's three-state default) — do we
  parse and honour the string `"unchanged"` or just treat any non-bool
  as "leave it alone"? Tentative: explicit `"unchanged"` string.
- Should `RegenerateSshHostKeys` also emit the `.pub` lines into a
  reporting event in the form cloud-init uses (so existing console
  tooling can consume them)? Tentative: yes, same shape as cloud-init.
- Host-key generation is slow on weak guests (~3s for RSA-3072). Worth
  doing in parallel? Cloud-init does it sequentially; we should too
  unless a benchmark says otherwise.
