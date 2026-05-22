# How to write a cloud-config

A cloud-config is a YAML document whose first non-empty line is
`#cloud-config`. The agent parses it, then runs the modules that match
the keys you used.

```yaml
#cloud-config
hostname: my-guest
users:
  - name: alice
    plain_text_passwd: ChangeMe!42
    groups: [Administrators]
write_files:
  - path: C:\demo\hello.txt
    content: "hello"
runcmd:
  - powershell.exe -NoProfile -Command "Write-Host 'hi'"
```

## Supported top-level keys

The agent recognises one key per module. The
[modules reference](../reference/modules.md) is the canonical list with
every sub-field.

| Key | Module | Notes |
| --- | --- | --- |
| `hostname`, `fqdn`, `preserve_hostname` | `SetHostname` | Sets Windows ComputerName. Reboots if needed. |
| `users`, `groups` | `UsersGroups` | Creates local users and groups; honors `sudo: true` -> Administrators. |
| `chpasswd`, `password` | `SetPasswords` | `RANDOM` type generates a 16-char password (never logged). |
| `ssh_authorized_keys` (top level), `users[].ssh_authorized_keys` | `SshAuthorizedKeys` | Top-level keys go to the first sudo-enabled user, else `Administrator`. |
| `write_files` | `WriteFiles` | Supports `b64`, `gz`, `gz+b64` encodings. POSIX `permissions` octal mapped onto NTFS ACLs. |
| `runcmd` | `Runcmd` | Runs in declaration order; exit code 1003 = reboot-and-continue. |

The script-style cloud-config keys (`scripts/per-instance`, etc.) are
delivered as MIME parts, not as cloud-config keys. See
[Run shell scripts](run-shell-scripts.md).

## Hostname (`SetHostname`)

```yaml
hostname: web01
# fqdn: web01.example.com    # alternative; first label becomes the NetBIOS name
# preserve_hostname: true    # explicitly do nothing
```

The Windows ComputerName cap is 15 chars; the agent doesn't truncate.
A name change requests a reboot, which the agent honors via the
reboot-and-continue path.

## Users and groups (`UsersGroups`)

```yaml
groups:
  - name: ops
    members: [alice]
users:
  - name: alice
    plain_text_passwd: SuperSecret!1
    lock_passwd: false
    groups: [Administrators]
    sudo: true        # any non-"false" string -> Administrators
  - name: bob
    passwd: AlsoSecret!1
    groups: [Users]
```

- `plain_text_passwd` and `passwd` are both treated as plaintext on
  Windows (no hashes). If both are set, `plain_text_passwd` wins —
  matches cloud-init.
- `sudo` semantics: any non-`false` value adds the user to
  `Administrators`. The richer Linux sudoers entries are ignored.
- Listed `groups[]` that don't exist are created automatically.

## Passwords (`SetPasswords`)

```yaml
chpasswd:
  users:
    - name: alice
      type: RANDOM        # 16 char random; not logged
    - name: bob
      password: BobP!42
  list: |
    carol:CarolP!42
    dave:DaveP!42
password: TopLevelP!42    # shorthand: applied to the first user, else Administrator
```

`SetPasswords` runs *after* `UsersGroups`, so a `chpasswd` entry
overrides the password the user record set.

## SSH authorized keys (`SshAuthorizedKeys`)

```yaml
ssh_authorized_keys:
  - ssh-ed25519 AAAA... fleet
users:
  - name: alice
    ssh_authorized_keys:
      - ssh-ed25519 AAAA... alice@laptop
```

The top-level list lands on the first sudo-enabled user, or on
`Administrator` if none exist. Per-user lists land on their respective
users.

## Write files (`WriteFiles`)

```yaml
write_files:
  - path: C:\demo\hello.txt
    content: "hello"
  - path: C:\demo\config.json
    encoding: b64
    content: eyJrIjogInYifQ==
  - path: C:\demo\bundle.tar
    encoding: gz+b64
    content: H4sIAA...
  - path: C:\demo\restricted.txt
    content: "secret"
    permissions: "0640"
    owner: alice
```

- POSIX paths (`/etc/...`) are translated to Windows (`C:\etc\...`) and
  rejected if they try to escape the C:\ root via `..`.
- `permissions` is a POSIX octal; the agent maps the three triplets onto
  NTFS ACLs (owner, group->Users, others->Everyone) the same way
  cloudbase-init does. SYSTEM and Administrators always keep
  FullControl.
- `append: true` opens for append; the default overwrites.

## Run commands (`Runcmd`)

```yaml
runcmd:
  - powershell.exe -NoProfile -Command "Write-Host 'shell-style string'"
  - [powershell.exe, -NoProfile, -Command, "Write-Host 'argv-style list'"]
```

String entries are dispatched to the shell (`cmd.exe /c …`); list
entries become argv exactly as written. Non-zero exits log an error and
continue with the next entry; exit code **1003** triggers a
reboot-and-continue (the run resumes after the reboot).

## Network configuration

Network-config is not part of cloud-config — it's a separate document
the datasource ships alongside user-data. See
[Configure networking](configure-networking.md).

## Validate before shipping

```powershell
egs-service validate --user-data C:\Temp\sample.yaml
```

Exit codes: 0 = valid, 1 = schema rejected, 2 = couldn't parse.
