# Modules

Each module owns one area of guest configuration. They run in a fixed order,
grouped into stages, and each runs at a frequency (per-instance, per-boot, or
per-once). Which modules run in a stage is configurable; their order is not.

| Module | Stage / order | Frequency | Cloud-config keys |
| --- | --- | --- | --- |
| Growpart | Network / 0 | per-boot | `growpart` |
| SetHostname | Network / 1 | per-instance | `hostname`, `fqdn`, `preserve_hostname`, `prefer_fqdn_over_hostname` |
| ApplyNetworkConfig | Network / 2 | per-instance | network-config document (not cloud-config) |
| NtpClient | Network / 3 | per-instance | `ntp` |
| Timezone | Network / 4 | per-instance | `timezone` |
| SetLocale | Network / 5 | per-instance | `locale`, `keyboard` |
| UsersGroups | Config / 0 | per-instance | `users`, `groups` |
| SetPasswords | Config / 1 | per-instance | `chpasswd`, `password` |
| SshModule | Config / 2 | per-instance | `ssh_authorized_keys`, `ssh_pwauth`, `ssh_keys`, `disable_root`, `ssh` |
| WriteFiles | Config / 3 | per-instance | `write_files` |
| Runcmd | Config / 4 | per-instance | `runcmd` |
| Licensing | Config / 5 | per-instance | `license` |
| WriteFilesDeferred | Final / before scripts | per-instance | `write_files` entries with `defer: true` |
| ScriptsUser | Final | per-instance | script payloads from MIME / shebang |
| PowerState | Final / last | per-instance | `power_state` |

To run a different set of modules in a stage, use the `stages` block in
[settings](settings.md). There are no Local-stage modules; the stage exists for
future use.

## Growpart

Extends the OS partition into unallocated space at the end of the disk. It runs
per-boot, not per-instance, because the host can enlarge the underlying VHD
between reboots and the next boot should pick up the new space.

```yaml
growpart:
  mode: auto            # auto | off
  devices: ['/']        # '/' is the system drive
```

`devices` takes `/` (the system drive), a drive letter (`C`, `"C:"`, `"D:\"`),
or `all`. Drive letters with a colon must be quoted, or YAML reads `- C:` as a
mapping. `all` never touches system, reserved, or recovery partitions.

## SetHostname

Sets the Windows computer name (the unqualified NetBIOS name). If only `fqdn` is
given, its first label is used.

```yaml
hostname: web01
```

`preserve_hostname: true` skips the module. `prefer_fqdn_over_hostname: true`
takes the first label of `fqdn` even when `hostname` is also set. When the name
change needs a reboot, provisioning suspends, the guest reboots, and the run
resumes afterward.

## ApplyNetworkConfig

Applies a network-config document (v1 or v2) to the guest. The document comes
from the datasource — for example inside a `config-2` ConfigDrive — not from
cloud-config. See [Configure networking](../howto/configure-networking.md).

Adapters are matched by MAC address. For each match the module applies the
addresses (IPv4 and IPv6, static or DHCP), gateways, routes, DNS servers and
search suffixes, and MTU. An entry whose MAC matches no adapter is skipped with
a warning; an absent network-config is a no-op.

## NtpClient

Configures the Windows Time service (`w32time`): start mode, network-availability
triggers, and the manual peer list.

```yaml
ntp:
  enabled: true
  servers: [time.windows.com]
  pools:   [pool.ntp.org]
  real_time_clock_utc: true    # optional, opt-in
```

`servers` and `pools` are combined into one peer list — Windows draws no
distinction. `enabled: false` stops the service and disables it.
`real_time_clock_utc` writes the `RealTimeIsUniversal` registry value; leave it
unset to keep the Windows default (it's only needed on hosts that keep the RTC
in UTC).

## Timezone

Sets the system timezone. Accepts an IANA name (`Europe/Berlin`) or a Windows
timezone key (`W. Europe Standard Time`); the IANA-to-Windows mapping ships with
.NET. An unrecognized value fails rather than being ignored.

```yaml
timezone: Europe/Berlin
```

## SetLocale

Sets the display language, culture, and keyboard layout. `locale` and `keyboard`
share one module because on Windows the language list and input method are tied
together.

```yaml
locale: de-DE
keyboard:
  layout: en-US
```

The two are independent — a keyboard-only change (German layout, English UI) is
fine. Changing the system locale needs a reboot, which the module requests only
when that value actually changed.

## UsersGroups

Creates local groups and users, sets passwords, and adds users to groups.

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

On Windows, `passwd` and `plain_text_passwd` are treated the same (no hash is
applied); `plain_text_passwd` wins if both are set. `sudo: true` (or any value
other than `false`) adds the user to `Administrators` — Linux sudoers rules are
ignored. `lock_passwd: true` disables the account. `gecos` sets the account's
full name (the "Full name" column in `lusrmgr.msc`).

## SetPasswords

Runs after `UsersGroups`, so a `chpasswd` entry overrides a password set on the
user record.

```yaml
chpasswd:
  users:
    - name: bob
      password: BobP!42
  list: |
    carol:CarolP!42
password: TopLevelP!42      # applies to the default user
```

Random passwords aren't supported — `type: RANDOM`, the `chpasswd.list`
`R`/`RANDOM` tokens, and password-less entries are rejected at validate and
skipped at runtime. cloud-init returns a generated password over the system
console, which Windows guests can't reliably offer here, so the value would be
lost. Set an explicit password.

`chpasswd.expire` flags changed passwords for change at next login. It defaults
to `true` (as in cloud-init) and applies to all three forms above; set it to
`false` to keep the password as-is.

## SshModule

Configures the guest's own OpenSSH server under `C:\ProgramData\ssh\`. This is
the standard network sshd — separate from the Hyper-V remote-access transport
that `egs-tool` uses.

```yaml
ssh_pwauth: false
disable_root: true
ssh_authorized_keys:
  - ssh-ed25519 AAAA... fleet
users:
  - name: alice
    ssh_authorized_keys:
      - ssh-ed25519 AAAA... alice
ssh:
  install_openssh: true
```

Top-level `ssh_authorized_keys` (together with any keys the datasource supplies)
go to the default user; per-user keys go to each named user. Keys are merged into
the existing `authorized_keys`, not replaced.

On the first boot the module generates host keys (`ed25519`, `ecdsa`, `rsa`;
override with `ssh_genkeytypes`), or writes the keys you supply in `ssh_keys`.
It writes a drop-in at `sshd_config.d\50-eryph.conf` for
`PasswordAuthentication` (from `ssh_pwauth`) and, when `disable_root: true`, a
`DenyUsers` entry for the built-in Administrator. The daemon restarts only when
host keys or the config changed.

If no sshd is installed the module still writes `authorized_keys` and skips the
rest, unless `ssh.install_openssh: true`, which installs the Win32-OpenSSH
server first.

## WriteFiles

Writes files, creating parent directories as needed.

```yaml
write_files:
  - path: C:\demo\config.json
    encoding: b64
    content: eyJrIjogInYifQ==
  - path: C:\demo\restricted.txt
    content: secret
    permissions: "0640"
    owner: alice
```

`encoding` accepts plain text (the default), `b64`/`base64`, `gz`/`gzip`, and
`gz+b64`. A POSIX path is translated to a Windows path under `C:\`; a path that
escapes via `..` is rejected and fails the module. POSIX `permissions` are mapped
to NTFS ACLs (owner, group as Users, others as Everyone; SYSTEM and Administrators
always keep full control), and `owner` sets the file owner.

Entries marked `defer: true` are left for `WriteFilesDeferred`.

## Runcmd

Runs each entry in order. A string runs through `cmd.exe`; a list is an argument
vector passed straight to the process.

```yaml
runcmd:
  - powershell.exe -NoProfile -Command "Write-Host 'shell'"
  - [powershell.exe, -NoProfile, -Command, "Write-Host 'argv'"]
```

A command that exits non-zero is logged and the next one still runs — the module
doesn't stop on the first failure (this matches cloud-init). To gate the rest on
a step, handle the error inside that command.

### Reboot-and-continue (exit 1001 / 1003)

| Exit | Meaning |
| --- | --- |
| `0` | success — go to next entry |
| `1001` | reboot the guest, then go to next entry |
| `1003` | reboot the guest, then re-run **this same entry** afterwards |
| any other non-zero | log error and continue with next entry |

`1002` is not supported and is treated as an error. The same exit-code contract
applies to user scripts under [ScriptsUser](#scriptsuser).

When an entry reboots the guest, completed entries are skipped on the next boot
and only the entry that asked is re-run. Editing an entry between boots (its
command text or argv) makes it run again from scratch.

### Per-script reboot quota

Each entry can ask for at most `reboot.maxPerScript` reboots (default 10)
before the run fails. The same cap applies to ScriptsUser scripts.

A script can raise its own cap from inside the entry by emitting this line on
stdout (last occurrence wins; the value must be a positive integer larger than
the current cap):

```
##egs.reboot_limit=20
```

The raised cap is persisted, so emit it once. To disable the override entirely,
set `reboot.allowScriptOverride: false` in [settings](settings.md).

### Environment exposed to the script

| Variable | Meaning |
| --- | --- |
| `EGS_ENTRY_INDEX` | 1-based position of this entry in `runcmd:` (or the script's ordinal under ScriptsUser) |
| `EGS_REBOOT_COUNT` | reboots this entry / script has already triggered (0 on the first run) |
| `EGS_REBOOT_LIMIT` | the current per-script reboot cap |

```powershell
switch ($env:EGS_REBOOT_COUNT) {
    '0' { Install-Feature-A; exit 1003 }
    '1' { Install-Feature-B; exit 1003 }
    '2' { Verify-All;        exit 0 }
}
```

## Licensing

Activates Windows. It runs by default with safe behaviour: it installs the AVMA
key for the guest's edition (a no-op on editions without one), and runs
`slmgr /rearm` only when the active product is an evaluation. On Azure it skips
activation — Windows activates against the Azure KMS automatically — but still
rearms evaluation editions, which Azure does not handle.

```yaml
license:
  product_key: AAAAA-BBBBB-CCCCC-DDDDD-EEEEE   # explicit; bypasses auto-detect
  kms_host:    "kms.example.com:1688"
  set_avma: true       # default true
  set_kms:  false       # default false
  activate: false       # default false
  rearm:    true        # default true; only on evaluation editions
  force:    false       # default false; run activation even on Azure
```

Priority is `product_key`, then AVMA, then KMS auto-detect. The key tables cover
Server 2012 R2 through 2025.

## WriteFilesDeferred

Runs the `write_files` entries marked `defer: true`, in the Final stage, after
users and groups exist.

```yaml
write_files:
  - path: /home/alice/.ssh/authorized_keys
    content: ssh-ed25519 AAAA... alice
    permissions: "0600"
    owner: alice
    defer: true
```

Defer an entry when its owner is a user created in the same run — at Config time
that account may not exist yet. Everything else works as in `WriteFiles`.

## ScriptsUser

Stages every script (from multipart parts or a top-level shebang) under
`%ProgramData%\eryph\provisioning\scripts\per-instance\` and runs it. The runner
is chosen by filename extension — see
[Run shell scripts](../howto/run-shell-scripts.md). Each script's output is
written to a per-script log and reported to the host.

Exit codes `1001` and `1003` and the `##egs.reboot_limit=N` directive work the
same way as in [Runcmd](#runcmd): a script can ask for one or more reboots,
inspect `EGS_REBOOT_COUNT` / `EGS_REBOOT_LIMIT` / `EGS_ENTRY_INDEX` in its
environment, and raise its own per-script cap (`reboot.maxPerScript`, default
10) by emitting the directive on stdout.

## PowerState

Reboots, powers off, or hibernates the guest at the end of provisioning — after
every other module has run. This is the scheduled "finish, then reboot" case,
distinct from the mid-run `1003` reboot.

```yaml
power_state:
  mode: reboot          # reboot | poweroff | halt
  delay: 30             # seconds
  message: "Provisioning done"
  condition: true       # optional; a command whose success gates the action
```

`mode: halt` hibernates, since Windows has no halt. `condition` may be a boolean
or a command — the action runs only if the command succeeds.
