# RFC 0024 — `power_state` cloud-config module

Status: Draft

## Problem

cloud-config operators expect a `power_state:` block to request a
reboot, poweroff, or halt at the END of provisioning — e.g. "install
all the software in Final, then reboot the catlet so Windows picks up
the new TrustedInstaller state". Today eryph fodder uses a `runcmd`
entry like `shutdown.exe /r /t 0`, which fires while the Config or
Final stage is still mid-flight and races with the rest of the agent.
There is no clean end-of-Final controlled-state-change hook.

Mid-stage reboot (exit code 1003 from runcmd / scripts; see RFC 0007)
already exists for "reboot then resume the same stage"; `power_state`
is the opposite: "finish everything, then go down on the operator's
schedule, with their message, their timeout."

## What cloud-init does

`cc_power_state_change` runs LAST in Final and shells out to
`shutdown` / `systemctl poweroff` / `halt` with the configured delay
and message. Optionally evaluates a `condition` (`true`/`false` or a
shell command whose exit code gates the action). Default frequency:
per-instance.

Cloud-init reference:
<https://cloudinit.readthedocs.io/en/latest/reference/modules.html#power-state-change>

## What cloudbase-init does

No direct equivalent. Genes that want a final reboot embed
`shutdown.exe /r /t 0` in `runcmd`.

## Schema

Mirror cloud-init's block; semantics adapt to `shutdown.exe`:

```yaml
power_state:
  mode: reboot                  # reboot | poweroff | halt
  delay: now                    # 'now' | '+N' minutes | absolute time
  message: 'Provisioning complete'
  timeout: 30                   # seconds to wait before forcing
  condition: true               # bool or shell command (exit 0 = proceed)
```

Mapping to `shutdown.exe`:

- `mode: reboot` → `/r`
- `mode: poweroff` → `/s`
- `mode: halt` → `/h` (hibernate; closest Windows analogue — log a
  Warning that halt-without-hibernate doesn't exist on Windows)
- `delay: now` → `/t 0`; `+N` (minutes) → `/t (N * 60)`. Absolute
  time strings (`"23:59"`) are parsed and converted to seconds-from-now.
- `message: ...` → `/c "<msg>"` (truncated to 512 chars by Windows).
- `timeout` is the soft-graceful window we honour before issuing the
  shutdown; mapped onto `/t`. Cloud-init's `timeout` is the SIGTERM →
  SIGKILL window, which has no direct Windows equivalent.

## What Windows needs

- `shutdown.exe` is the canonical entry point; available on every
  supported Windows SKU.
- Requires `SeShutdownPrivilege`. The agent runs as LocalSystem, which
  holds it by default — no privilege juggling needed.
- `condition`: when string, run via the same path as `runcmd` shell
  entries (`IWindowsOs.RunShellCommandAsync`). Exit 0 = proceed; any
  other exit = skip the power-state change and log Info.

## Tentative direction

### POCO sketch

```csharp
public sealed record PowerStateConfig
{
    public string? Mode { get; init; }            // "reboot" | "poweroff" | "halt"
    public string? Delay { get; init; }           // "now" | "+N" | "HH:mm"
    public string? Message { get; init; }
    public int? Timeout { get; init; }            // seconds
    public object? Condition { get; init; }       // bool OR string (cloud-init shape)
}
```

`Condition` is `object?` to preserve cloud-init's untyped union;
deserialisation normalises to `(bool? Literal, string? Command)`.

### Module

- `Eryph.GuestServices.Provisioning.Modules.PowerStateModule`
  (`[Stage(Stage.Final, Order = int.MaxValue - 1, Frequency = …)]`).
- Final stage, ordered LAST so every other Final module has already
  written its semaphores and logs before the system goes down.
- New `IWindowsOs.RequestPowerStateAsync(PowerStateAction, …)` wraps
  `shutdown.exe` invocation; `DryRunWindowsOs` records the call and
  does not actually reboot.
- Logging: emit Information("scheduled reboot in {Sec}s: {Msg}")
  immediately after issuing `shutdown.exe`, then return Ok so the
  StageRunner records the semaphore BEFORE Windows starts tearing
  down the agent process.

### Frequency — the awkward question

Cloud-init's `cc_power_state_change` is per-instance. On a per-instance
schedule the marker is written when the module returns Ok — but if the
operator wrote `condition: true` AND the action fires, the next boot
will see the per-instance semaphore already present and skip the
module. That's correct: the reboot was for the first-boot
provisioning, the operator does not want another reboot on every boot.

**Open question — make this explicit in code:** must the semaphore
write happen BEFORE `shutdown.exe` is invoked? If it happens after, a
race window exists where Windows tears the process down between
`shutdown.exe` returning and the semaphore being flushed to disk.
Tentative: yes, write the semaphore first, then call `shutdown.exe`;
matches cloud-init's intent.

## Open questions

- Per-boot vs per-instance: a "reboot every boot" recipe is
  nonsensical (infinite loop), so per-instance is right for v1.
  Document the loop-avoidance reasoning so it doesn't get changed
  later.
- `condition` as a shell string runs ONCE before the action. Cloud-init
  documents but barely uses this. We support it for fidelity; reject
  excessively long commands (>1 KB) in `egs-service validate`.
- Should the module short-circuit when the agent is running under
  `egs-tool` interactive (a human invoked provisioning manually)?
  Surprising to lose your session to a `shutdown /r /t 0`. Tentative:
  honour the config; an operator who set `power_state:` knew what they
  asked for.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — exit-1003
  "reboot and continue" is the MID-stage cousin of this module.
- [RFC 0010](0010-semaphore-design.md) — per-instance semaphore
  written before issuing the shutdown so the post-reboot run does not
  loop.
