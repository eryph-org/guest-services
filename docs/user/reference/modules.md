# Modules

Modules are the units of work the agent runs. Each declares the stage
it belongs to and a frequency (per-instance / per-boot / per-once).

This page lists every module that ships in v1, in the order they run.

| Module | Stage / Order | Frequency | Cloud-config keys |
| --- | --- | --- | --- |
| `SetHostname` | Network / 1 | per-instance | `hostname`, `fqdn`, `preserve_hostname` |
| `ApplyNetworkConfig` | Network / 2 | per-instance | (network-config document, not cloud-config) |
| `UsersGroups` | Config / 0 | per-instance | `users`, `groups` |
| `SetPasswords` | Config / 1 | per-instance | `chpasswd`, `password` |
| `SshAuthorizedKeys` | Config / 2 | per-instance | `ssh_authorized_keys`, `users[].ssh_authorized_keys` |
| `WriteFiles` | Config / 3 | per-instance | `write_files` |
| `Runcmd` | Config / 4 | per-instance | `runcmd` |
| `ScriptsUser` | Final / 0 | per-instance | (script payloads from MIME / shebang) |

There are no Local-stage modules in v1; the slot exists for future use.

---

## `SetHostname`

**Stage:** Network, Order 1. **Frequency:** per-instance.

Sets the Windows ComputerName (the unqualified NetBIOS name). Uses the
first label of `fqdn` if `hostname` is unset.

`preserve_hostname: true` makes the module a no-op. If the name change
requires a reboot, the module returns `RebootRequested` — the run is
suspended, `shutdown /r /t 5` is invoked, and the agent resumes on the
next boot via the per-instance semaphore.

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

Design: [RFC 0002](../../rfcs/0002-network-config-v1-v2-application.md).

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

---

## `SetPasswords`

**Stage:** Config, Order 1. **Frequency:** per-instance.

Runs after `UsersGroups`, so a `chpasswd` entry overrides the password
the user record set — matches cloud-init.

```yaml
chpasswd:
  users:
    - name: alice
      type: RANDOM
    - name: bob
      password: BobP!42
  list: |
    carol:CarolP!42
    dave:DaveP!42
password: TopLevelP!42    # shorthand; lands on the first user, else Administrator
```

`type: RANDOM` generates a 16-char password from a 70-glyph alphabet
(~98 bits of entropy) using `RandomNumberGenerator` with rejection
sampling. **The generated value is never logged.** A secret-channel
reporting event is planned but not in v1; for now operators harvest
the password out of band (e.g. via reset and re-set with a known value).

---

## `SshAuthorizedKeys`

**Stage:** Config, Order 2. **Frequency:** per-instance.

Writes the public keys into the OpenSSH-style
`authorized_keys` location for each target user. Top-level
`ssh_authorized_keys` lands on the first sudo-enabled user; if none
exists, on `Administrator`.

```yaml
ssh_authorized_keys:
  - ssh-ed25519 AAAA... fleet
users:
  - name: alice
    ssh_authorized_keys:
      - ssh-ed25519 AAAA... alice
```

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
always retain FullControl). **Note:** in v1 the module currently logs a
warning and skips the ACL translation — the wrapper exists in
`WindowsOs.SetPosixPermissionsAsync` but the module hasn't been wired
to call it. Track this as a known gap.

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

Exit code **1003** triggers reboot-and-continue (cloudbase-init
convention; cloud-init does not honor this). Non-zero (≠1003) logs an
error and continues with the next entry.

---

## `ScriptsUser`

**Stage:** Final, Order 0. **Frequency:** per-instance.

Stages every script payload (collected from multipart MIME parts or a
top-level shebang) under
`%ProgramData%\eryph\provisioning\scripts\per-instance\` and runs them.

Dispatch is filename-led — see
[Run shell scripts](../howto/run-shell-scripts.md) and
[RFC 0007](../../rfcs/0007-scripts-per-frequency-edge-cases.md). Stdout
and stderr are captured to per-script log files and emitted as a
`ReportingEvent.Progress` per script.

Exit code 1003 → reboot-and-continue, same as `Runcmd`.
