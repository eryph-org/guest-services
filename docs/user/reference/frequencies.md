# Module frequencies

Every module declares one of three frequencies. The frequency decides
**when** the module is gated by a semaphore.

| Frequency | Runs | Semaphore lives in | Cleared by `reset` |
| --- | --- | --- | --- |
| `per-instance` | Once per instance id | `instance\<instance-id>\sem\<module>.per-instance` | yes (default) |
| `per-boot` | Once per OS boot session | `sem\<module>.per-boot` | yes (default) |
| `per-once` | Exactly once, ever | `sem\<module>.per-once` | **no** (use `--reset-once`) |

All v1 modules declare `per-instance` — they configure host state from
the cloud-config and shouldn't re-apply every boot.

The frequency is set on the `[Stage(...)]` attribute on each module
class.

## How semaphores gate execution

Before invoking a module's `ApplyAsync`, the runner asks
`ISemaphoreStore.ExistsAsync(module, frequency, instanceId)`:

- Marker present → log Info "skipping; semaphore exists" and skip.
- Marker absent → run the module. On `Completed` or `RebootRequested`,
  write the marker before returning so the post-reboot pass doesn't
  loop. On `Failed`, no marker is written; the module re-runs next pass.

Marker files contain a JSON blob (`timestamp`, `instanceId`, `outcome`)
that's purely diagnostic — only the file's *existence* matters for
gating.

## Boot session detection

`per-boot` semaphores are cleared at the start of every new boot.
"New boot" is decided by comparing
`Win32_OperatingSystem.LastBootUpTime` (via CIM) against a marker file:

```
%ProgramData%\eryph\provisioning\last-seen-boot.json
```

If the CIM read fails (WMI service stopped), the detector defaults to
"new boot" — running a per-boot module twice is idempotent by contract;
suppressing one that should have run is not.

## Cloud-init parity

The directory shape matches cloud-init's almost exactly, modulo the
Windows path prefix:

| Cloud-init | Agent |
| --- | --- |
| `/var/lib/cloud/instance/sem/<module>.per-instance` | `%ProgramData%\eryph\provisioning\instance\<id>\sem\<module>.per-instance` |
| `/var/lib/cloud/sem/<module>.per-boot` | `%ProgramData%\eryph\provisioning\sem\<module>.per-boot` |
| `/var/lib/cloud/sem/<module>.per-once` | `%ProgramData%\eryph\provisioning\sem\<module>.per-once` |

`<module>` is the fully-qualified .NET type name (e.g.
`Eryph.GuestServices.Provisioning.Modules.UsersGroupsModule`).
`<instance-id>` is sanitised against
`Path.GetInvalidFileNameChars()` before being used in a path.

Design notes: [RFC 0003](../../rfcs/0003-module-frequencies.md) +
[RFC 0010](../../rfcs/0010-semaphore-design.md).

## Forcing a re-run

| Goal | Command |
| --- | --- |
| Re-run every per-instance module | `egs-service reset` |
| Re-run a single module | Delete the matching `.per-instance` file by hand, then `run`. |
| Re-run a per-once module | `egs-service reset --reset-once` |
| Don't reset per-boot during a reset | `egs-service reset --keep-per-boot` |

See [Reset and re-run](../howto/reset-and-rerun.md) for the full
matrix.
