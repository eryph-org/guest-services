# Settings (`egs-provisioning.json`)

Optional tunables for the agent. If no file is found, defaults are
used.

## Lookup order

1. `egs-provisioning.json` next to `egs-service.exe`.
2. `%ProgramData%\eryph\provisioning\settings.json`.

The first file that exists wins. A malformed file is **ignored** (the
agent falls back to defaults rather than failing the run); fix it and
re-run.

## Full example

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
    "maxPerScript": 2
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

Property names are case-insensitive. Trailing commas and `//` comments
are allowed.

## `userData` — pipeline tunables

| Key | Default | Meaning |
| --- | --- | --- |
| `maxRecursionDepth` | `10` | Cap on `#include` / nested multipart recursion. |
| `fetchTimeoutSeconds` | `30` | Per-attempt timeout when fetching `#include` URLs. |
| `fetchMaxAttempts` | `4` | Total attempts (initial + retries) per URL. |
| `fetchInitialBackoffSeconds` | `1` | Doubles up to a 4s cap between retries. |
| `fetchMaxBytes` | `10485760` (10 MiB) | Max size of a single `#include` response. A larger `Content-Length`, or a body that streams past this cap (server lying about / omitting the header), aborts the fetch. |

## `dataSources` — locator tunables

| Key | Default | Meaning |
| --- | --- | --- |
| `readinessTimeoutMinutes` | `15` | Total wall-clock budget for `LocateAsync` across **all** sources. When exhausted, the run exits with `NoDataSource`. |
| `minBackoffSeconds` | `1` | Floor for the `WaitForReady` backoff. |
| `maxBackoffSeconds` | `60` | Cap on exponential growth between retries. |
| `dataSourceList` | _(unset)_ | Ordered list of datasource names to probe (e.g. `["NoCloud","ConfigDrive","Azure"]`), mirroring cloud-init's `datasource_list`. See below. |

`dataSourceList` (the JSON key is camelCase, like every other setting)
mirrors cloud-init's `datasource_list`:

- **Unset / empty** (default): every registered source is probed in
  `Priority` order — see [Datasources](datasources.md).
- **Set**: only the named sources are probed, **in the listed order**
  (priority is ignored for selection). Matching is case-insensitive on
  the source name.
- Names that match no registered source are logged at **Warning** and
  ignored — a typo never crashes the run.
- If *every* name is unknown, the locator logs a Warning and falls back
  to all sources in `Priority` order, so a fully-typo'd list cannot
  silently disable provisioning.

Per-source overrides are not exposed in v1.

## `scripts` — `ScriptsUser` module

| Key | Default | Meaning |
| --- | --- | --- |
| `perInstanceDirectory` | `%ProgramData%\eryph\provisioning\scripts\per-instance` | Where user-data scripts are staged before execution. Environment variables are expanded. |
| `scriptTimeoutMinutes` | `60` | Per-script timeout. **Not enforced in v1** — reserved. |

## `reboot` — loop-safety caps

Two independent guards against a runaway "reboot-and-continue" (exit
code 1003) loop. See
[bug 0001](../../bugs/0001-scriptsusermodule-skips-queue-after-reboot.md)
"loop-safety".

| Key | Default | Meaning |
| --- | --- | --- |
| `maxPerModule` | `3` | Max times a single module may return `RebootRequested` before the `StageRunner` fails the run rather than re-entering it. The **outer** backstop. |
| `maxPerScript` | `2` | Max reboots a single `ScriptsUser` script (keyed by ordinal + body hash) may request before that script is failed. The **inner**, tighter guard. |

The two caps are deliberately separate: `maxPerScript` bounds one
script, while `maxPerModule` bounds the whole `ScriptsUser` module
across all of its scripts. A module reaching `maxPerModule` while no
individual script has hit `maxPerScript` (e.g. three different scripts
each reboot once) still trips the outer cap. Keep `maxPerScript` ≤
`maxPerModule`.

## `defaultUser` — image-baked default admin

The cloud-init `system_info.default_user` analogue. Top-level credential
shorthands (`ssh_authorized_keys`, `password`, `chpasswd`) target the
*resolved* default user, picked in this order:

1. The first sudo-enabled user in the cloud-config `users:` block.
2. A datasource-supplied default user name.
3. `defaultUser.name` from these settings.
4. `Administrator`.

| Key | Default | Meaning |
| --- | --- | --- |
| `name` | _(unset)_ | Image-baked default admin name. Unset → the `Administrator` fallback. |
| `groups` | `["Administrators"]` | Groups for the default user when it has to be created. |
| `createIfMissing` | `false` | Auto-create the default user (and apply the top-level credentials to it) when no `users:` entry declares one. |

## `stages` — per-stage module allow/deny

Mirrors cloud-init's `cloud_init_modules` / `cloud_config_modules` /
`cloud_final_modules`. Keys are stage names (`Local`, `Network`,
`Config`, `Final`; case-insensitive). When a stage is absent, all its
modules run.

| Key | Meaning |
| --- | --- |
| `enabledModules` | When set, only the named modules run in this stage. |
| `disabledModules` | Named modules are removed (applied after `enabledModules` when both are set). |

Module names are case-insensitive and tolerate the `Module` suffix
(`SetHostname` and `SetHostnameModule` both match). Stage membership and
intra-stage order are fixed by the module attributes; these lists only
add or remove. Unknown names are logged at Warning, never fatal.

## Service control (registry)

Two operator on/off switches turn the top-level guest-services capabilities
off. They are **registry-based, not part of `egs-provisioning.json`**: they are
host/operator controls (the operator decides what a guest is allowed to do),
separate from the provisioning tunables above.

Key: `HKLM\SOFTWARE\eryph\guest-services`. Both values are `REG_DWORD`.

| Value | Capability gated | Default |
| --- | --- | --- |
| `ProvisioningEnabled` | The cloud-init-style first-boot provisioning agent. When off, no user-data is applied at boot. | on |
| `RemoteAccessEnabled` | The remote-access transport — the Hyper-V-vsock SSH server `egs-tool` connects to for shell / exec / file transfer. When off, the transport is not started. | on |

Both are **opt-out**: a capability is **on** when the value is absent (or set to
any non-zero number), and **off only** when set to an explicit `0`. A missing
key, a read error, or a non-Windows host all leave the capability on (fail-open
— a registry problem never silently disables a capability).

Turn a capability off (PowerShell, elevated):

```powershell
New-Item -Path 'HKLM:\SOFTWARE\eryph\guest-services' -Force | Out-Null
# disable first-boot provisioning
Set-ItemProperty -Path 'HKLM:\SOFTWARE\eryph\guest-services' -Name 'ProvisioningEnabled' -Type DWord -Value 0
# disable the remote-access SSH transport
Set-ItemProperty -Path 'HKLM:\SOFTWARE\eryph\guest-services' -Name 'RemoteAccessEnabled' -Type DWord -Value 0
```

Set the value back to `1` (or delete it) to re-enable. The flags are read at
service start, so restart the `eryph guest services` service after changing
them.

## Not configurable

- Per-datasource overrides (e.g. `dataSources.azure.timeoutMinutes`).
- Jinja2 / vendor-data merge knobs.
