# Modules

Modules are the units of work the agent runs. Each declares its stage
and a frequency (per-instance / per-boot / per-once). They run in this
order:

| Module | Stage / Order | Frequency | Cloud-config keys |
| --- | --- | --- | --- |
| `Growpart` | Network / 0 | per-boot | `growpart` |
| `SetHostname` | Network / 1 | per-instance | `hostname`, `fqdn`, `preserve_hostname`, `prefer_fqdn_over_hostname` |
| `ApplyNetworkConfig` | Network / 2 | per-instance | (network-config document, not cloud-config) |
| `NtpClient` | Network / 3 | per-instance | `ntp` |
| `Timezone` | Network / 4 | per-instance | `timezone` |
| `SetLocale` | Network / 5 | per-instance | `locale`, `keyboard` |
| `UsersGroups` | Config / 0 | per-instance | `users`, `groups` |
| `SetPasswords` | Config / 1 | per-instance | `chpasswd`, `password` |
| `SshModule` | Config / 2 | per-instance | `ssh_authorized_keys`, `users[].ssh_authorized_keys`, `ssh_pwauth`, `ssh_keys`, `ssh_genkeytypes`, `disable_root`, `ssh` |
| `WriteFiles` | Config / 3 | per-instance | `write_files` (entries with `defer: true` are skipped here) |
| `Runcmd` | Config / 4 | per-instance | `runcmd` |
| `Licensing` | Config / 5 | per-instance | `license` |
| `WriteFilesDeferred` | Final / -1 | per-instance | `write_files` (entries with `defer: true` only) |
| `ScriptsUser` | Final / 0 | per-instance | (script payloads from MIME / shebang) |
| `PowerState` | Final / last | per-instance | `power_state` |

Stage membership and intra-stage order are fixed by the `[Stage]` /
`[Order]` attributes, but which modules run in a stage is operator-
configurable — see the `stages` block in [Settings](settings.md).

There are no Local-stage modules; the slot exists for future use.

## Acknowledged-but-no-op keys

Not a module — handled inside `CloudConfigSerializer` at deserialize
time. Logs at Info every cloud-init top-level key the agent accepts but
does not act on, mirroring cloud-init's log-and-continue handling of
cross-cloud YAML. Paste a Linux cloud-config with `apt: { sources: ... }`
and `packages: [git, vim]` and you get one "saw it, Linux concept,
ignored" line per key instead of a Warning.

The keys handled here are **schema fields** on the parsed CloudConfig
(stored as `object?` for the polymorphic shapes) — the deserializer
accepts them without complaint. Truly unknown top-level keys (typos,
unrecognised extensions) still hit the same `CloudConfigSerializer`
on the Warning channel.

Acknowledged keys:

- Linux package management: `apt`, `apt_pipelining`, `packages`,
  `package_update`, `package_upgrade`, `package_reboot_if_required`,
  `snap`, `yum_repos`, `yum_repo_dir`
- Linux disk / filesystem / mount: `disk_setup`, `fs_setup`, `mounts`
- Linux network / hosts: `manage_etc_hosts`, `manage_resolv_conf`, `resolv_conf`
- Deferred cloud-init modules: `bootcmd`, `phone_home`,
  `final_message`, `ca_certs`, `disable_root`, `disable_root_opts`
- Configuration-management bootstraps (future): `chef`, `ansible`,
  `puppet`, `salt_minion`

---

## `Growpart`

**Stage:** Network, Order 0. **Frequency:** per-boot.

Extends the OS partition into any unallocated space at the end of the
disk. Per-boot (not per-instance) because the host can enlarge the
underlying VHD between reboots, and the operator expects the next boot
to consume the new capacity.

```yaml
growpart:
  mode: auto            # auto | off (also accepts YAML boolean `false`)
  devices: ['/']        # default; '/' resolves to the Windows system drive
```

`devices` accepts `/` (the system drive — `%SystemDrive%`), a drive
letter (`C`, `"C:"`, `"D:\"`), or `all` (every growable volume; never
extends system / reserved / recovery partitions). Drive letters with a
colon **must be quoted** — `- C:` parses as a YAML mapping.

---

## `SetHostname`

**Stage:** Network, Order 1. **Frequency:** per-instance.

Sets the Windows ComputerName (the unqualified NetBIOS name). Uses the
first label of `fqdn` if `hostname` is unset.

`preserve_hostname: true` makes the module a no-op. If the name change
requires a reboot, the module returns `RebootRequested` — the run is
suspended, `shutdown /r /t 5` is invoked, and the agent resumes on the
next boot via the per-instance semaphore.

`prefer_fqdn_over_hostname: true` swaps the precedence: when both
`hostname` and `fqdn` are set, the first label of `fqdn` wins. With the
flag absent or `false`, the default (hostname-first, fqdn-fallback)
holds. Cloud-init parity.

```yaml
#cloud-config
hostname: web01
```

---

## `ApplyNetworkConfig`

**Stage:** Network, Order 2. **Frequency:** per-instance.

Applies a cloud-init **network-config** document (v1 or v2) to the
Windows guest. The document lives on the datasource (e.g. inside the
`config-2` ConfigDrive), not in cloud-config. See
[Configure networking](../howto/configure-networking.md).

Matches adapters by MAC address. Disables DHCP before applying static
addresses. Honors `nameservers.addresses` and `mtu`.

Skipped silently when no network-config is present. Skipped per-entry
(with a warning) when MAC is missing or doesn't match any adapter.

---

## `NtpClient`

**Stage:** Network, Order 3. **Frequency:** per-instance.

Configures the Windows Time service (`w32time`) — start mode, SCM
triggers, manual peer list — and optionally the
`RealTimeIsUniversal` registry value (for guests where the host RTC is
UTC).

```yaml
ntp:
  enabled: true                  # default true
  servers: [time.windows.com]
  pools:   [pool.ntp.org]
  real_time_clock_utc: true      # optional; opt-in only
```

`servers` and `pools` are concatenated into a single `w32tm` manual
peer list (Windows doesn't distinguish). `enabled: false` stops
w32time and sets its start mode to `Disabled`. Triggers are reset so
the service follows network availability (cbi parity).

---

## `Timezone`

**Stage:** Network, Order 4. **Frequency:** per-instance.

Sets the system timezone. Accepts either an IANA identifier
(`Europe/Berlin`) or a Windows timezone key name
(`W. Europe Standard Time`). IANA → Windows translation uses the CLDR
mapping shipped with .NET.

```yaml
timezone: Europe/Berlin
```

Unknown identifiers (neither IANA nor Windows) return a clear failure
instead of silently no-op'ing.

---

## `SetLocale`

**Stage:** Network, Order 5. **Frequency:** per-instance.

Sets the display language / culture / keyboard layout. The
`locale` and `keyboard` cloud-init keys are handled by one module
because on Windows the language list and input method are tightly
coupled.

```yaml
locale: de-DE                    # BCP-47 culture name
keyboard:
  layout: en-US                  # BCP-47 or "lang:KLID" form
```

Both fields are independent: keyboard-only (operator wants QWERTZ but
leaves the UI in English) is fine. Changing the system locale
(`Set-WinSystemLocale` — the non-Unicode ANSI codepage) requires reboot;
the module returns `RebootRequested` only when that specific value
changed.

---

## `UsersGroups`

**Stage:** Config, Order 0. **Frequency:** per-instance.

Creates local groups, creates local users, sets passwords from
`users[].passwd` / `users[].plain_text_passwd`, adds users to groups,
and (when `sudo` is non-`false`) ensures the user is in the local
`Administrators` group.

```yaml
groups:
  - name: ops
    members: [alice]
users:
  - name: alice
    plain_text_passwd: ChangeMe!42
    groups: [ops, Administrators]
    sudo: true
```

Windows-specific notes:

- `plain_text_passwd` and `passwd` are treated identically (no hashes
  apply on Windows). `plain_text_passwd` wins if both are set, matching
  cloud-init.
- `sudo: true` (or any non-`false` value) → user added to
  `Administrators`. Linux sudoers entries are ignored.
- `lock_passwd: true` disables the account.
- `gecos: "Full Name"` maps to the NTUser `FullName` field — visible as
  "Full name" in `lusrmgr.msc`. Cloud-init mirrors the same value into
  `/etc/passwd`'s comment column on Linux.

---

## `SetPasswords`

**Stage:** Config, Order 1. **Frequency:** per-instance.

Runs after `UsersGroups`, so a `chpasswd` entry overrides the password
the user record set — matches cloud-init.

```yaml
chpasswd:
  users:
    - name: bob
      password: BobP!42
  list: |
    carol:CarolP!42
    dave:DaveP!42
password: TopLevelP!42    # shorthand; lands on the resolved default user
```

**Random passwords are not supported.** cloud-init's `type: RANDOM`
(and the `chpasswd.list` `R`/`RANDOM` tokens, and a password-less
`chpasswd.users` entry) generate a password and deliver it by writing
it to `/dev/console`. Windows guests have no console channel reliably
captured across the clouds eryph targets, so a generated password could
never be retrieved — setting one would silently lock the operator out.
`egs-service validate` rejects these, and at runtime they are
warn-and-skipped. Specify an explicit password instead.

`chpasswd.expire` controls the "must change at next logon" flag. The
cloud-init default is `true` — every changed password is flagged for
change at first login. `expire: false` suppresses the flag. The default
applies to all three input forms: `chpasswd.users`, `chpasswd.list`,
and the top-level `password` shorthand.

---

## `SshModule`

**Stage:** Config, Order 2. **Frequency:** per-instance.

The cloud-init `cc_ssh` equivalent for the OS-level Win32-OpenSSH daemon
under `C:\ProgramData\ssh\`. This is the host OS sshd — distinct from the
egs-service Hyper-V-vsock remote-access transport `egs-tool` connects to.

```yaml
ssh_pwauth: false                 # PasswordAuthentication in the drop-in (omitted if unset)
disable_root: true                # DenyUsers the built-in Administrator (resolved by RID-500 SID)
ssh_authorized_keys:
  - ssh-ed25519 AAAA... fleet
users:
  - name: alice
    ssh_authorized_keys:
      - ssh-ed25519 AAAA... alice
ssh:
  install_openssh: true           # install the Win32-OpenSSH server if absent
```

What it does:

- **authorized_keys.** Top-level `ssh_authorized_keys` (merged with any
  datasource-supplied public keys) land on the resolved default user;
  per-user `users[].ssh_authorized_keys` land on each named user. Keys
  are **merged** into the existing `authorized_keys`, not overwritten.
- **Host keys.** Generates host keys (`ed25519`, `ecdsa`, `rsa` by
  default; override with `ssh_genkeytypes`) on the first instance boot,
  or writes operator-supplied `ssh_keys` verbatim. DSA is skipped
  (removed in OpenSSH 9.8).
- **sshd_config drop-in.** Writes
  `C:\ProgramData\ssh\sshd_config.d\50-eryph.conf`:
  `PasswordAuthentication` from `ssh_pwauth` (omitted when unset),
  always `PubkeyAuthentication yes`, and `DenyUsers <Administrator>` when
  `disable_root: true`.
- **Install.** `ssh.install_openssh: true` installs the Win32-OpenSSH
  server when no sshd is present. Without it, the module just writes
  `authorized_keys` (sshd reads them per-connection once one exists) and
  skips host-key / config / restart work.
- **Fingerprints.** Regenerated host-key fingerprints are reported
  unless `ssh.emit_keys_to_console: false`.

The daemon restarts only when host keys or the drop-in actually changed.

---

## `WriteFiles`

**Stage:** Config, Order 3. **Frequency:** per-instance.

Writes files to disk; creates parent directories as needed; rejects
path-traversal (`..`) attempts.

```yaml
write_files:
  - path: C:\demo\config.json
    encoding: b64
    content: eyJrIjogInYifQ==
  - path: C:\demo\bundle.tar
    encoding: gz+b64
    content: H4sIAA...
  - path: C:\demo\restricted.txt
    content: secret
    permissions: "0640"
    owner: alice
    append: false
```

Encodings: empty / `text/plain` (UTF-8 bytes), `b64` / `base64`,
`gz` / `gzip`, `gz+b64` and its aliases. Unknown encoding fails the
single entry; the module continues with the next.

POSIX paths starting with `/` are translated to Windows paths under
`C:\`. Paths that try to escape via `..` are rejected and the module
**fails** (the module returns Fail rather than silently skipping a
suspicious entry).

POSIX `permissions` is mapped onto NTFS ACLs the same way cloudbase-init
does (owner / group→Users / others→Everyone; SYSTEM and Administrators
always retain FullControl) via `WindowsOs.SetPosixPermissionsAsync`,
which also applies `owner`. When only `owner` is set, the existing ACL is
left alone and just the owner changes. An ACL failure logs a warning and
the entry's content write still stands.

Entries flagged `defer: true` are skipped by this Config-stage module
and handled by `WriteFilesDeferred` at Final — see below.

---

## `WriteFilesDeferred`

**Stage:** Final, Order -1 (runs before `ScriptsUser`). **Frequency:** per-instance.

Final-stage counterpart to `WriteFiles`. Picks up only the
`write_files` entries flagged `defer: true` and writes them after
users / groups / passwords have been processed. Cloud-init parity —
matches `cc_write_files_deferred.py`.

```yaml
write_files:
  - path: /home/alice/.ssh/authorized_keys
    content: ssh-ed25519 AAAA... alice
    permissions: "0600"
    owner: alice
    defer: true
```

Use this when an entry depends on state earlier modules in the same
run create — typically when the file must be owned by a user the
`users` block created in the same run. Without `defer: true`, the
write fires at Config order 3 and the owner principal may not yet
exist; deferring to Final guarantees `UsersGroups` (Config order 0)
has already created the principal.

All other semantics (encoding, path translation, POSIX permissions)
match `WriteFiles`.

---

## `Runcmd`

**Stage:** Config, Order 4. **Frequency:** per-instance.

Runs each entry in declaration order. String entries go through
`cmd.exe`-style shell dispatch; list (argv) entries are executed
verbatim.

```yaml
runcmd:
  - powershell.exe -NoProfile -Command "Write-Host 'shell-style'"
  - [powershell.exe, -NoProfile, -Command, "Write-Host 'argv-style'"]
```

**Failure contract:** commands run in declaration order. A non-zero
exit (≠1003) is **logged as an error and execution continues with the
next command** — the module does not abort on first failure. This is
cloud-init parity; operators coming from "shell scripts stop on first
error" need the heads-up. If you need a command to gate the rest, make
it fail the script itself (e.g. a single multi-line entry with your own
error handling).

Exit code **1003** triggers reboot-and-continue (cloudbase-init
convention; cloud-init does not honor this) — the module returns and
the runner resumes after the reboot.

---

## `Licensing`

**Stage:** Config, Order 5. **Frequency:** per-instance.

Activates Windows. Module is **always-on** with safe-by-default
behaviour:

- `set_avma` defaults to **true** — installs the AVMA key for the
  guest's edition (silent no-op on non-Server SKUs or editions not in
  our table).
- `rearm` defaults to **true** — fires `slmgr /rearm` only when the
  active product is an evaluation (`SoftwareLicensingProduct.IsEvaluation`).
  Returns `RebootRequested` on success — rearm needs reboot to apply.
- On Azure (active datasource = `Azure`), the activation path skips
  itself: Windows on Azure activates against the Azure internal KMS
  automatically. The rearm path still runs because Azure does NOT
  manage evaluation grace periods.

Operators can override any default:

```yaml
license:
  # Explicit overrides (highest priority — auto-detect is bypassed):
  product_key: AAAAA-BBBBB-CCCCC-DDDDD-EEEEE
  kms_host:    "kms.example.com:1688"

  # Auto-detect against the guest edition:
  set_avma: true            # default true
  set_kms:  false           # default false; when true, clears KMS host so DNS SRV takes over

  # Extras:
  activate: false           # default false — KMS clients self-activate
  rearm:    true            # default true — only fires on evaluation editions
  force:    false           # default false — apply activation path even on Azure
```

Resolution priority: `product_key` > AVMA > KMS auto. AVMA / KMS key
tables are verified against the Microsoft Learn AVMA and KMS reference
pages — covers Server 2012 R2 through 2025 (Datacenter / Standard /
Solution / Datacenter:Azure Edition where applicable).

---

## `ScriptsUser`

**Stage:** Final, Order 0. **Frequency:** per-instance.

Stages every script payload (collected from multipart MIME parts or a
top-level shebang) under
`%ProgramData%\eryph\provisioning\scripts\per-instance\` and runs them.

Dispatch is filename-led — see
[Run shell scripts](../howto/run-shell-scripts.md). Stdout
and stderr are captured to per-script log files and emitted as a
`ReportingEvent.Progress` per script.

Exit code 1003 → reboot-and-continue, same as `Runcmd`.

---

## `PowerState`

**Stage:** Final, Order `int.MaxValue - 1` (runs LAST). **Frequency:** per-instance.

Optional controlled reboot / poweroff / hibernate at the END of
provisioning. Distinct from exit-1003 reboot-and-continue, which is
mid-stage. Use this when the operator wants "finish everything, then
reboot on my schedule."

```yaml
power_state:
  mode: reboot                 # reboot | poweroff | halt (default: reboot)
  delay: now                   # now | +N (minutes) | HH:MM | integer (seconds)
  message: 'Provisioning complete'
  timeout: 30                  # accepted for cloud-init parity; no Windows mapping
  condition: true              # bool or shell command (exit 0 = proceed)
```

`mode` notes:

- `poweroff` accepts `shutdown` as a cbi-style alias.
- `halt` has no clean Windows analogue; falls back to hibernate
  (`shutdown.exe /h`) with a Warning logged.

`delay` always honours a 5-second minimum so the StageRunner cleanup
(per-instance semaphore + KVP "completed" event + datasource cleanup
hook) has time to flush before Windows starts tearing the agent down.

`condition`:

- `null` / omitted → proceed.
- `true` / `false` → literal proceed / skip.
- string → run as a shell command (`cmd.exe /c <…>`); exit 0 proceeds,
  anything else skips.

The module returns `Completed`, not `RebootRequested` — per-instance
semaphore stops the post-reboot run from re-entering and scheduling
another shutdown (otherwise: infinite reboot loop).
