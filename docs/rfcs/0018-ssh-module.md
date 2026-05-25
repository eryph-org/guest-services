# RFC 0018 — Windows OpenSSH daemon configuration (`SshModule`)

Status: Implemented (`SshModule` — host keys, sshd_config.d drop-in, disable_root→RID-500, install opt-in, fingerprint reporting; merging authorized_keys; `DefaultUserResolver`)

## Problem

`SshAuthorizedKeysModule` today only writes per-user `authorized_keys`
files. Cloud-init's `cc_ssh` is much broader: it manages host keys
(generate fresh on first boot, or import operator-supplied), edits
`sshd_config` (`PasswordAuthentication`, `PermitRootLogin`), and surfaces
the host-key fingerprints via reporting. We promise "behaves like
cloud-init / cbi" but we silently dropped most of that block.

There are **two SSH servers** on an eryph guest, and they must not be
confused:

1. The **egs-service Hyper-V-socket transport** (DevTunnels, documented in
   [RFC 0008](0008-platform-native-provisioner-coexistence.md)). This is the
   eryph control channel. This RFC does **not** touch it.
2. The **OS-level Win32-OpenSSH `sshd`** at `C:\ProgramData\ssh\`. This is the
   general-purpose SSH server an operator's cloud-config configures, and the
   one this RFC is about.

## What cloud-init does

`cc_ssh` covers
(<https://cloudinit.readthedocs.io/en/latest/reference/modules.html#ssh>):

1. **Host keys.** `ssh_keys:` writes the supplied keypairs to
   `/etc/ssh/ssh_host_<type>_key{,.pub}`. Otherwise generate fresh keys
   (`ssh_genkeytypes`); `ssh_deletekeys: true` deletes pre-existing keys
   first (the "stripped image, fresh identity per instance" path).
2. **Authorized keys.** Top-level + per-user `ssh_authorized_keys`, merged
   into each user's `~/.ssh/authorized_keys`.
3. **sshd_config edits.** `ssh_pwauth` toggles `PasswordAuthentication`;
   `disable_root: true` blocks root login.
4. **Reporting.** Emits host-key fingerprints so an operator console can show
   them; `ssh.emit_keys_to_console` gates console output.

The cloud-init account model relevant here is `system_info.default_user` —
the image-baked admin that top-level credential shorthands target when no
user is named.

## What cloudbase-init does

Cbi does not manage Win32-OpenSSH; its closest plugin is `createuser.py`
(writes `authorized_keys`-shaped data into a profile). We extend past cbi
here — the gene corpus already configures sshd via fodder scripts, and we
absorb that work.

## Decision

### 1. Drop-in config, not main-file mutation

We do **not** rewrite `C:\ProgramData\ssh\sshd_config`. Instead:

- Idempotently ensure `Include sshd_config.d/*.conf` is present **at the top**
  of `sshd_config`. sshd uses *first-obtained-value*, so the Include must
  precede both the shipped directives and the trailing
  `Match Group administrators` block for our settings to win.
- Write all our settings to `C:\ProgramData\ssh\sshd_config.d\50-eryph.conf`.

`IWindowsOs.EnsureSshdConfigIncludeAsync` is idempotent (a commented-out
Include does not count); `WriteSshdDropInAsync` writes the drop-in UTF-8/LF.

### 2. authorized_keys MERGE + dedup (Finding 6)

`SetUserSshAuthorizedKeysAsync` now **merges** the supplied keys into the
existing file instead of overwriting it. Dedup is by the normalized key body
(`<type> <base64>`, ignoring options prefix and trailing comment); existing
order is preserved and genuinely-new keys are appended. Empty input with no
existing file is a no-op (no empty file is created). This closes review
Finding 6 (overwrite clobbered image-baked / prior-run keys).

### 3. ed25519-first, DSA dropped

Default host-key set is `[ed25519, ecdsa, rsa]`, RSA with a **3072-bit floor**.
DSA was removed in OpenSSH 9.8; `dsa` and unknown types are warned and
skipped by `RegenerateSshHostKeysAsync`.

### 4. Detect-don't-install by default

The module configures an already-installed sshd. Installation is opt-in via
`ssh.install_openssh: true`, which runs
`Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0` and sets the
`sshd` service start mode to Automatic (`InstallOpenSshServerAsync`, no-op
when already installed). `install_openssh` is an **eryph extension**, not part
of cloud-init's `cc_ssh` schema.

### 5. Host-key ACL hardening in code

Written/generated host keys are ACL-hardened in managed code (owner SYSTEM;
SYSTEM + Administrators FullControl; inheritance disabled; nothing else),
replicating the `administrators_authorized_keys` pattern. We do **not** shell
out to `FixHostFilePermissions.ps1`. sshd refuses to start if a private host
key is readable by anyone else.

### 6. `disable_root` → built-in Administrator (RID 500)

The Linux `disable_root` → "block root login" maps on Windows to denying
OpenSSH login for the **built-in Administrator** account. That account is
resolved by **well-known SID (RID 500)** so the lookup survives a rename
(`ResolveBuiltinAdministratorNameAsync` enumerates Administrators members and
returns the one whose SID ends `-500`; falls back to the literal
`"Administrator"` with a Warning if resolution fails). The result is written
as `DenyUsers <name>` in the drop-in.

This is the **OS-level privileged account** and is intentionally **separate**
from the configurable provisioning default user (below).

### 7. DefaultUser model (layered resolution)

The "who do top-level shorthands (`ssh_authorized_keys`, `password`, the
`chpasswd` shorthand) target" concept — the eryph analogue of cloud-init's
`system_info.default_user`. Resolved by `IDefaultUserResolver` in priority
order; first layer that yields a name wins:

1. **First admin/sudo-enabled user in `config.Users`** — uses the shared
   `SudoPolicy.IsSudoEnabled`, so the decision matches `UsersGroupsModule`'s
   Administrators-promotion exactly. An explicitly declared admin is the
   operator's clearest intent and always wins.
2. **Datasource-supplied default admin name** — a **documented stub seam**.
   `DataSourceResult.DefaultUserName` is wired through the resolver but is
   currently `null` everywhere; it stays inert until the ConfigDrive /
   OpenStack metadata work (Findings 19/20) populates it (OpenStack
   `admin_pass` / known-admin name).
3. **`settings.DefaultUser.Name`** — the image-baked default admin from
   `egs-provisioning.json` (cloud-init `system_info.default_user.name`).
4. **`"Administrator"` fallback.**

`DefaultUserSettings` also carries `Groups` (null → `["Administrators"]`) and
`CreateIfMissing` (auto-create + apply top-level creds when the user-data
declares no admin).

### 8. Typed reporting event

`ReportingEvent.SshHostKeysReported(IReadOnlyList<SshHostKeyFingerprint>)`
carries the generated host-key fingerprints. The `LogReportingHandler` logs
one line per fingerprint; the `KvpReportingHandler` writes a compact
`type=fingerprint;...` string under `eryph.provisioning.ssh_host_keys`. The
producing site gates emission on `ssh.emit_keys_to_console`.

`SshHostKeyFingerprint` is `(string KeyType, string Fingerprint, string
PublicKey)` — `Fingerprint` is the `SHA256:...` form from `ssh-keygen -l`.

## Model surface

The structured block goes under `ssh:` (`SshConfig`):

- `emit_keys_to_console` (`bool?`, all platforms) — cloud-init key; on Windows
  gates the `SshHostKeysReported` event.
- `install_openssh` (`bool?`, Windows-only) — eryph extension.

These existing top-level keys flip from Linux-only to all-platforms with real
Windows behaviour (the SshModule implements them): `ssh_pwauth`, `ssh_keys`,
`ssh_deletekeys`, `ssh_genkeytypes`, `ssh_publish_hostkeys`, `disable_root`.
`ssh_import_id` stays Linux-only — there is no Windows fetch path yet.

## IWindowsOs additions

```csharp
Task<bool> IsSshdInstalledAsync(CancellationToken ct);
Task InstallOpenSshServerAsync(CancellationToken ct);
Task<IReadOnlyList<SshHostKeyFingerprint>> RegenerateSshHostKeysAsync(
    IReadOnlyList<string> keyTypes, bool deleteExisting, CancellationToken ct);
Task WriteSshHostKeyAsync(string keyType, string privatePem, string? publicLine, CancellationToken ct);
Task EnsureSshdConfigIncludeAsync(CancellationToken ct);
Task WriteSshdDropInAsync(string dropInFileName, string contents, CancellationToken ct);
Task RestartSshdAsync(CancellationToken ct);
Task<string> ResolveBuiltinAdministratorNameAsync(CancellationToken ct);
// reworked to merge+dedup:
Task SetUserSshAuthorizedKeysAsync(string userName, IReadOnlyList<string> keys, CancellationToken ct);
```

`DryRunWindowsOs` logs/no-ops the writes and passes the reads through.

## Module (leaf-agent work, not this foundation)

`SshAuthorizedKeysModule` is renamed/broadened to `SshModule` (Stage = Config,
Frequency = PerInstance) covering the surface above:

1. If `ssh.install_openssh`, install + set Automatic.
2. Ensure the `Include` + write `50-eryph.conf` (`ssh_pwauth`, `disable_root`
   → `DenyUsers`).
3. Host keys: write `ssh_keys` verbatim (ACL-hardened) else regenerate
   `ssh_genkeytypes` (default `[ed25519, ecdsa, rsa]`); honour
   `ssh_deletekeys`.
4. authorized_keys (top-level → DefaultUser, per-user → each user) via the
   merging writer.
5. Restart sshd if anything changed; if sshd isn't installed, log Warning and
   continue.
6. Report fingerprints via `SshHostKeysReported` (gated by
   `emit_keys_to_console`).

## Don't break the egs-service transport

The egs-service SSH server (Hyper-V socket) is a separate process and is not
influenced by `C:\ProgramData\ssh\sshd_config`. The `SshModule` edits only the
OS-level OpenSSH config.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — runcmd vs module
  ordering (sshd restart happens here, not in operator scripts).
- [RFC 0008](0008-platform-native-provisioner-coexistence.md) — the
  egs-service SSH transport is separate from OS-level OpenSSH.
- [RFC 0009](0009-module-list-split.md) — operators who manage sshd themselves
  disable via `disabledModules: [SshModule]`.

## Open questions

- `ssh_pwauth: "unchanged"` (cloud-init's three-state default) — we honour the
  explicit `"unchanged"` string as "leave the directive alone"; omitted is
  also "leave alone".
- Host-key generation is sequential (cloud-init does likewise). Parallelism is
  deferred unless a benchmark on weak guests justifies it.
