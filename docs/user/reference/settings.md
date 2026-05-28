# Settings (`egs-provisioning.json`)

Optional tunables. With no settings file, the agent uses its defaults.

It looks first for `egs-provisioning.json` next to `egs-service.exe`, then for
`%ProgramData%\eryph\provisioning\settings.json`. The first one found wins. A
malformed file is ignored — the agent falls back to defaults rather than failing
the run.

```json
{
  "userData": {
    "maxRecursionDepth": 10,
    "fetchTimeoutSeconds": 30,
    "fetchMaxAttempts": 4,
    "fetchInitialBackoffSeconds": 1,
    "fetchMaxBytes": 10485760
  },
  "dataSources": {
    "readinessTimeoutMinutes": 15,
    "minBackoffSeconds": 1,
    "maxBackoffSeconds": 60,
    "dataSourceList": ["NoCloud", "ConfigDrive", "Azure"]
  },
  "scripts": {
    "perInstanceDirectory": "%ProgramData%\\eryph\\provisioning\\scripts\\per-instance",
    "scriptTimeoutMinutes": 60
  },
  "reboot": {
    "maxPerModule": 3,
    "maxPerScript": 10,
    "allowScriptOverride": true
  },
  "defaultUser": {
    "name": "Administrator",
    "groups": ["Administrators"],
    "createIfMissing": false
  },
  "stages": {
    "Config": { "disabledModules": ["Licensing"] },
    "Final":  { "enabledModules": ["ScriptsUser"] }
  }
}
```

Property names are case-insensitive; trailing commas and `//` comments are
allowed.

## userData

| Key | Default | Meaning |
| --- | --- | --- |
| `maxRecursionDepth` | 10 | Cap on `#include` and nested-multipart recursion. |
| `fetchTimeoutSeconds` | 30 | Per-attempt timeout when fetching an `#include` URL. |
| `fetchMaxAttempts` | 4 | Attempts (initial plus retries) per URL. |
| `fetchInitialBackoffSeconds` | 1 | Backoff between retries, doubling up to 4s. |
| `fetchMaxBytes` | 10485760 (10 MiB) | Largest `#include` response accepted; a bigger one aborts the fetch. |

## dataSources

| Key | Default | Meaning |
| --- | --- | --- |
| `readinessTimeoutMinutes` | 15 | Total budget for finding a datasource across all sources. |
| `minBackoffSeconds` | 1 | Floor for the retry backoff while a source isn't ready. |
| `maxBackoffSeconds` | 60 | Cap on the retry backoff. |
| `dataSourceList` | unset | Datasource names to probe, in order. |

When `dataSourceList` is unset, every source is probed in priority order (see
[Datasources](datasources.md)). When set, only the named sources are probed, in
the order given. Matching is case-insensitive; a name that matches no source is
logged and skipped, and if every name is unknown the agent falls back to probing
all sources so a typo can't disable provisioning. Valid names: `Azure`, `EC2`,
`NoCloud`, `ConfigDrive`, `OpenStack` (the last is experimental — see
[Datasources](datasources.md)).

## scripts

| Key | Default | Meaning |
| --- | --- | --- |
| `perInstanceDirectory` | `%ProgramData%\eryph\provisioning\scripts\per-instance` | Where user-data scripts are staged. Environment variables are expanded. |
| `scriptTimeoutMinutes` | 60 | Per-script timeout. Not enforced yet. |

## reboot

Caps for the cbi-style reboot-and-continue (exit `1001` / `1003`) convention.

| Key | Default | Meaning |
| --- | --- | --- |
| `maxPerModule` | 3 | How many times a module may ask for a reboot of its own accord before the run fails. Reboots requested by `runcmd` entries or user scripts do not count here — those are bounded by `maxPerScript`. |
| `maxPerScript` | 10 | How many reboots one piece of user code (a `runcmd` entry or a `ScriptsUser` script) may ask for before it is failed. |
| `allowScriptOverride` | true | When true, user code can raise its own per-script cap on this run with `##egs.reboot_limit=<n>` on stdout. The raised cap is persisted across reboots so the directive only needs to be emitted once. |

## defaultUser

The account that top-level `ssh_authorized_keys`, `password`, and `chpasswd`
shorthands apply to when the cloud-config doesn't name a user. It's resolved in
order: the first sudo-enabled user in `users:`, then a name supplied by the
datasource, then `defaultUser.name`, then `Administrator`.

| Key | Default | Meaning |
| --- | --- | --- |
| `name` | unset | The default admin name. Unset falls back to `Administrator`. |
| `groups` | `["Administrators"]` | Groups to put the default user in if it has to be created. |
| `createIfMissing` | false | Create the default user (and apply the top-level credentials) when no `users:` entry declares one. |

## stages

Selects which modules run in a stage — cloud-init's `cloud_init_modules` /
`cloud_config_modules` / `cloud_final_modules`. Keys are stage names (`Local`,
`Network`, `Config`, `Final`); a stage that isn't listed runs all its modules.

| Key | Meaning |
| --- | --- |
| `enabledModules` | Only the named modules run in this stage. |
| `disabledModules` | The named modules are removed (applied after `enabledModules`). |

Module names are case-insensitive and accept the `Module` suffix (`SetHostname`
or `SetHostnameModule`). These lists only add or remove — stage membership and
order are fixed. An unknown name is logged, not fatal.

## Service control

Operator switches that turn top-level capabilities off. They live outside
`egs-provisioning.json` because they're host controls separate from the
provisioning tunables above.

| Value | Turns off | Default |
| --- | --- | --- |
| `ProvisioningEnabled` | The first-boot provisioning agent — no user-data is applied. | on |
| `RemoteAccessEnabled` | The remote-access transport `egs-tool` connects to. | on |
| `KvpAuthEnabled` | Honoring of authorized client keys delivered via Hyper-V data exchange. When off, only the locally provisioned key in `id_egs.pub` authorizes — pushes via `egs-tool add-ssh-config` and other KVP writers are ignored. | on |

All three are opt-out: a capability is on unless the value is set to `0`. A
missing value, a read error, or an unknown OS all leave it on. The flags are
read at service start, so restart `eryph-guest-services` after changing them.

### Windows

Stored in the registry under `HKLM\SOFTWARE\eryph\guest-services` as
`REG_DWORD` values (`0` = off, anything else = on). REG_SZ and other value
types are ignored — only REG_DWORD is the documented control surface.

```powershell
New-Item -Path 'HKLM:\SOFTWARE\eryph\guest-services' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\eryph\guest-services' -Name 'ProvisioningEnabled' -Type DWord -Value 0
Set-ItemProperty -Path 'HKLM:\SOFTWARE\eryph\guest-services' -Name 'RemoteAccessEnabled' -Type DWord -Value 0
Set-ItemProperty -Path 'HKLM:\SOFTWARE\eryph\guest-services' -Name 'KvpAuthEnabled' -Type DWord -Value 0
Restart-Service -Name eryph-guest-services
```

### Linux

Stored in `/etc/opt/eryph/guest-services/service-control.conf` as `KEY=VALUE`
lines (`0` / `false` = off, case-insensitive; anything else, missing key, or
missing file = on). Blank lines and `#` comments are ignored.

```bash
sudo install -d /etc/opt/eryph/guest-services
sudo tee /etc/opt/eryph/guest-services/service-control.conf <<EOF
ProvisioningEnabled=0
RemoteAccessEnabled=0
KvpAuthEnabled=0
EOF
sudo systemctl restart eryph-guest-services
```

Set values back to `1` (or delete the value / file) and restart to re-enable.
