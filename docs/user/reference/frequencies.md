# Module frequencies

Every module runs at one of three frequencies. The frequency decides how often
the module's work is allowed to happen, tracked by a semaphore file.

| Frequency | Runs | Semaphore | Cleared by `reset` |
| --- | --- | --- | --- |
| per-instance | Once per instance id | `instance\<instance-id>\sem\<module>.per-instance` | yes (default) |
| per-boot | Once per OS boot | `sem\<module>.per-boot` | yes (default) |
| per-once | Once, ever | `sem\<module>.per-once` | no (use `--reset-once`) |

Most modules are per-instance: they configure the guest from the cloud-config
and shouldn't reapply on every boot. `Growpart` is per-boot, so it picks up a
host-side disk resize between reboots.

## How gating works

Before running a module, the agent checks for its semaphore. If the marker
exists, the module is skipped. If not, it runs — and on success (or when it asks
for a reboot) the marker is written, so the module doesn't repeat after the
reboot. A module that fails leaves no marker and runs again on the next pass.

The marker file holds a small diagnostic JSON record (timestamp, instance id,
outcome); only its presence matters for gating.

## New boots

Per-boot markers are cleared at the start of each boot. The agent records the
last boot time at:

```
%ProgramData%\eryph\provisioning\last-seen-boot.json
```

and clears the per-boot markers when the current boot is newer. If it can't read
the boot time, it treats the boot as new — running a per-boot module twice is
harmless, skipping one that should run is not.

## Semaphore paths

The layout matches cloud-init under a Windows prefix:

| cloud-init | This agent |
| --- | --- |
| `/var/lib/cloud/instance/sem/<module>.per-instance` | `%ProgramData%\eryph\provisioning\instance\<id>\sem\<module>.per-instance` |
| `/var/lib/cloud/sem/<module>.per-boot` | `%ProgramData%\eryph\provisioning\sem\<module>.per-boot` |
| `/var/lib/cloud/sem/<module>.per-once` | `%ProgramData%\eryph\provisioning\sem\<module>.per-once` |

`<module>` is the module's full type name (for example
`Eryph.GuestServices.Provisioning.Modules.UsersGroupsModule`). The instance id
is sanitized for use in a path.

## Forcing a re-run

| Goal | Command |
| --- | --- |
| Re-run every per-instance module | `egs-service reset` |
| Re-run one module | Delete its `.per-instance` marker, then `run` |
| Re-run a per-once module | `egs-service reset --reset-once` |
| Keep per-boot markers during a reset | `egs-service reset --keep-per-boot` |

See [Reset and re-run](../howto/reset-and-rerun.md) for the full matrix.
