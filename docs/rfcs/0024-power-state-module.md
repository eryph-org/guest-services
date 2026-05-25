# RFC 0024 — `power_state` cloud-config module

Status: Implemented
Implemented in commit `<pending>`

## Problem

cloud-config operators expect a `power_state:` block to request a
reboot, poweroff, or halt at the END of provisioning — e.g. "install
all the software in Final, then reboot the catlet so Windows picks up
the new TrustedInstaller state". Before this module, eryph fodder used
a `runcmd shutdown.exe /r /t 0`, which fires while the Config or Final
stage is still mid-flight and races with the rest of the agent.

Mid-stage reboot (exit code 1003 from runcmd / scripts; see RFC 0007)
already exists for "reboot then resume the same stage"; `power_state`
is the opposite: "finish everything, then go down on the operator's
schedule, with their message, their timeout."

## What ships

### Schema

```yaml
power_state:
  mode: reboot                  # reboot | poweroff | halt (default: reboot)
  delay: now                    # now | +N (minutes) | HH:MM | integer (seconds)
  message: 'Provisioning complete'
  timeout: 30                   # accepted for cloud-init parity; no Windows mapping
  condition: true               # bool (true|false) or shell command (exit 0 = proceed)
```

POCO `PowerStateConfig`. `Mode` defaults to `reboot` — the operator
intent most commonly is "reboot when done". `Condition` is `object?`
because cloud-init's schema is a literal union of bool / string.

### Module

- `Eryph.GuestServices.Provisioning.Modules.PowerStateModule`
  (`Stage.Final`, `Order = int.MaxValue - 1`, `Frequency = PerInstance`).
- Runs LAST in Final so every other module's semaphore is written
  before Windows starts tearing the agent process down.
- Returns `ModuleOutcome.Completed`, **not** `RebootRequested`.
  Critical: returning RebootRequested would re-enter the module on
  the post-reboot run and schedule ANOTHER reboot — infinite loop.
  Per-instance semaphore + `Completed` outcome is the only safe
  combination.

### Mode mapping

| `mode:` | shutdown.exe flag | Notes |
|---|---|---|
| `reboot` (default) | `/r` | |
| `poweroff` / `shutdown` (cbi alias) | `/s` | |
| `halt` | `/h` | Hibernate — closest Windows analogue. Module logs a Warning explaining the substitution. |

`/f` (force-close apps) is always passed — we're unattended.

### Delay grammar

The module parses `delay:` against the cloud-init grammar AND clamps to
a minimum 5-second buffer so the StageRunner cleanup (semaphore +
KVP "completed" event + datasource cleanup hook) has time to flush
before Windows actually goes down.

- `now` → 5s (clamped from 0)
- `+N` → N minutes
- `HH:MM` → seconds until that time today (or tomorrow if past)
- integer → seconds
- anything else → `Failed` outcome with a clear message

### Condition evaluation

- `null` (omitted) → proceed
- YAML plain (unquoted) `true` / `false` → native `bool` → proceed / skip
- YAML quoted `"true"` / `"false"` → `string` → run as shell command
  (matches cloud-init's behaviour where `condition: "true"` invokes
  the `true` shell builtin; on Linux this also proceeds, by accident).
- Any other string → run via `IWindowsOs.RunShellCommandAsync`;
  exit 0 proceeds, anything else skips.

The plain-vs-quoted distinction matters because cloud-init / PyYAML
distinguishes them and we want behavioural parity. YamlDotNet's
default `object?`-target deserialisation discards the distinction
(everything comes back as `string`), so the YAML layer attaches
`BoolOrStringYamlConverter` to `PowerStateConfig.Condition` to inspect
`Scalar.Style` and restore PyYAML-equivalent type resolution.

### OS seam

```csharp
Task RequestPowerStateAsync(PowerStateRequest request, CancellationToken ct);

public sealed record PowerStateRequest
{
    public PowerStateAction Action { get; init; }
    public int DelaySeconds { get; init; }
    public string? Message { get; init; }
}
public enum PowerStateAction { Reboot, Poweroff, Halt }
```

`WindowsOs.RequestPowerStateAsync` shells `shutdown.exe` with the
appropriate flags. `DryRunWindowsOs.RequestPowerStateAsync` logs and
does NOT shutdown — the whole point of dry-run is "tell me what would
happen", and accidentally power-cycling the operator's host is the
worst possible bug to ship.

## What changed from the original Draft — and why

- **`Mode` now defaults to `reboot`** (Draft had no default).
  Operators writing `power_state: {}` clearly want SOMETHING to
  happen — null-mode-fails is unfriendly. cloud-init also defaults to
  reboot in practice (its `mode` is required by schema, but the
  ecosystem treats `reboot` as the default).
- **`/f` (force-close) always passed.** Draft was ambiguous. We're
  unattended; not forcing means shutdown can stall on an unsaved app
  dialog from a runcmd helper that happens to be still alive.
- **Minimum 5s delay buffer.** Draft asked the open question "must
  the semaphore write happen BEFORE `shutdown.exe`?" — resolved
  by clamping the delay so the StageRunner has guaranteed cleanup
  time after this module's `ApplyAsync` returns.
- **`condition: true|false` YAML-bool handling.** Draft assumed
  YamlDotNet would surface the bool as `bool` to an `object?` target;
  empirically it returns the raw scalar text as `string` and discards
  YAML's plain-vs-quoted distinction. Fixed at the YAML layer via
  `BoolOrStringYamlConverter` (attached to `PowerStateConfig.Condition`)
  rather than papering over in the module: plain `true` → bool;
  quoted `"true"` → string → shell command. Matches PyYAML / cloud-init
  behaviour exactly, including the edge case where quoted-bool gets
  treated as a shell command.
- **`shutdown` accepted as cbi-alias for `poweroff`.** Not in the
  Draft; added so cross-tool YAML works (cbi operators write
  `mode: shutdown`).
- **`power_state` removed from the acknowledged-key set in
  `CloudConfigSerializer`.** Was a no-op stub there until this module
  shipped; now it's a real implemented key.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — exit-1003
  reboot-and-continue is the MID-stage cousin.
- [RFC 0010](0010-semaphore-design.md) — per-instance semaphore
  written by the StageRunner after the module returns; the delay
  buffer protects the cleanup window.
- [RFC 0028](0028-linux-keys-module.md) — `power_state` was
  acknowledged-but-no-op there until this module shipped.
