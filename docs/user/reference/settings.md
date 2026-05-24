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
    "readinessTimeoutMinutes": 5,
    "minBackoffSeconds": 1,
    "maxBackoffSeconds": 60
  },
  "scripts": {
    "perInstanceDirectory": "%ProgramData%\\eryph\\provisioning\\scripts\\per-instance",
    "scriptTimeoutMinutes": 60
  },
  "reboot": {
    "maxPerModule": 3,
    "maxPerScript": 2
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
| `readinessTimeoutMinutes` | `5` | Total wall-clock budget for `LocateAsync` across **all** sources. When exhausted, the run exits with `NoDataSource`. |
| `minBackoffSeconds` | `1` | Floor for the `WaitForReady` backoff. |
| `maxBackoffSeconds` | `60` | Cap on exponential growth between retries. |

Per-source overrides are not exposed in v1. See
[RFC 0004](../../rfcs/0004-datasource-readiness-timeout.md).

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

## What's not in v1

- Per-stage module allow/deny lists. The module set is fixed; see
  [RFC 0009](../../rfcs/0009-module-list-split.md).
- Per-datasource overrides (e.g. `dataSources.azure.timeoutMinutes`).
- Jinja2 / vendor-data merge knobs.
