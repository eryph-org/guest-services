# Differences from cloud-init

The agent reuses cloud-init's stage names, module names, frequencies, semaphore
layout, and user-data formats. If you know cloud-init, most things work as you
expect. The differences that matter:

## Behaviour

| Topic | This agent |
| --- | --- |
| Script dispatch | By filename extension (`.ps1`, `.cmd`, `.bat`) first, shebang only as a fallback. cloud-init goes by the shebang. |
| Reboot during provisioning | A `runcmd` entry or script can exit `1001` (reboot, entry done) or `1003` (reboot, re-run same entry). cloud-init uses `power_state` for this. |
| `power_state: halt` | Falls back to hibernate — Windows has no halt equivalent. |
| `runcmd` argv form | Passed to the Windows process API verbatim, no shell re-quoting. A string entry goes through `cmd.exe`. |
| Jinja2 templating | Not supported. `## template: jinja` is ignored. |
| `#part-handler` | Not supported. |
| Boothooks | Stored, not run. |
| Quoted-printable parts | Not decoded; passed through as raw bytes. `base64`, `7bit`, `8bit`, `binary` are handled. |
| Random passwords | Not supported. `type: RANDOM`, the `chpasswd.list` `R`/`RANDOM` tokens, and password-less entries are rejected at validate and skipped at runtime. Use an explicit password. |
| Linux-only keys (`apt`, `snap`, `chef`, …) | Accepted and logged at Info; they do nothing on Windows. `egs-service validate --target windows` flags them. |
| Reporting | Log and Hyper-V KVP only. No webhook or cloud-native backend. |
| Vendor-data | Read and discarded; not merged into cloud-config. |

Random passwords are rejected rather than generated because cloud-init returns
the generated value over the system console (`/dev/console`), and Windows guests
have no console channel that is reliably captured across the clouds eryph
targets — a generated password would be unrecoverable.

## YAML

The agent reads cloud-config the way cloud-init does: PyYAML's `SafeLoader`
under YAML 1.1. Bool tokens (`yes`/`no`/`on`/`off`), integer forms, `bool | string`
unions, and merge keys all resolve identically. Two values are read differently,
on purpose:

A colon-bearing scalar stays a string. cloud-init reads `12:30` as the base-60
integer `750`; the agent keeps it as written, so unquoted times, ratios, and
`host:port` values mean what they look like.

A string-typed field keeps its literal text. cloud-init resolves the implicit
type and then converts back to a string, turning `hostname: NO` into `"False"`
and `version: 1.10` into `"1.1"`. The agent keeps what you wrote.

## Same as cloud-init

- Four stages (Local / Network / Config / Final) and the per-instance /
  per-boot / per-once frequencies.
- `reset` matches `cloud-init clean`; `collect-logs` matches
  `cloud-init collect-logs`.
- network-config v1 and v2, including IPv6, routes, DNS search domains, and MTU.
- `chpasswd.expire`, `write_files.defer`, `prefer_fqdn_over_hostname`, and
  `users[].gecos` (mapped to the Windows full name).
- Per-stage module selection (`enabledModules` / `disabledModules`) and
  datasource ordering (`dataSourceList`).
- Unknown top-level keys are logged, never fatal.
