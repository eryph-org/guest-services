# Write a cloud-config

A cloud-config is a YAML document whose first line is `#cloud-config`. The agent
parses it and runs the modules whose keys you used.

```yaml
#cloud-config
hostname: web01
users:
  - name: alice
    plain_text_passwd: ChangeMe!42
    groups: [Administrators]
    sudo: true
ssh_authorized_keys:
  - ssh-ed25519 AAAA... alice@laptop
write_files:
  - path: C:\demo\hello.txt
    content: hello
runcmd:
  - powershell.exe -NoProfile -Command "Write-Host 'hi'"
```

Each top-level key maps to a module:

| Key | Module |
| --- | --- |
| `growpart` | Growpart |
| `hostname`, `fqdn`, `preserve_hostname`, `prefer_fqdn_over_hostname` | SetHostname |
| `ntp` | NtpClient |
| `timezone` | Timezone |
| `locale`, `keyboard` | SetLocale |
| `users`, `groups` | UsersGroups |
| `chpasswd`, `password` | SetPasswords |
| `ssh_authorized_keys`, `ssh_pwauth`, `ssh_keys`, `disable_root`, `ssh` | SshModule |
| `write_files` | WriteFiles |
| `runcmd` | Runcmd |
| `license` | Licensing |
| `power_state` | PowerState |

The [modules reference](../reference/modules.md) documents each key's
sub-fields. A few things that aren't obvious on Windows:

- Passwords are plaintext (`passwd` and `plain_text_passwd` are the same here),
  and `sudo` with any value other than `false` adds the user to Administrators.
  Random passwords aren't supported — set an explicit one.
- `ssh_authorized_keys` at the top level go to the default user; per-user keys
  go to their users. Both are merged into any existing keys.
- `write_files` paths written as POSIX (`/etc/...`) are translated under `C:\`;
  `permissions` is mapped to an NTFS ACL.
- A `runcmd` entry that exits `1003` reboots the guest and resumes the run.

Network configuration is a separate document, not part of cloud-config — see
[Configure networking](configure-networking.md). Scripts are delivered as
multipart parts, not cloud-config keys — see
[Run shell scripts](run-shell-scripts.md).

Check a file before you ship it:

```powershell
egs-service validate --user-data C:\Temp\sample.yaml
```
