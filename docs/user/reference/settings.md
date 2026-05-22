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
    "fetchInitialBackoffSeconds": 1
  },
  "dataSources": {
    "readinessTimeoutMinutes": 5,
    "minBackoffSeconds": 1,
    "maxBackoffSeconds": 60
  },
  "scripts": {
    "perInstanceDirectory": "%ProgramData%\\eryph\\provisioning\\scripts\\per-instance",
    "scriptTimeoutMinutes": 60
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

## What's not in v1

- Per-stage module allow/deny lists. The module set is fixed; see
  [RFC 0009](../../rfcs/0009-module-list-split.md).
- Per-datasource overrides (e.g. `dataSources.azure.timeoutMinutes`).
- Jinja2 / vendor-data merge knobs.
