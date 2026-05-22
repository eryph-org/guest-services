# Stages

The agent runs four stages, in order: `Local`, `Network`, `Config`,
`Final`. Names and intent mirror cloud-init.

| Stage | Modules (v1) | Purpose |
| --- | --- | --- |
| `Local` | (none) | Platform setup before networking is available. Reserved for future use (`disk_setup`, etc.). |
| `Network` | `SetHostname` (1), `ApplyNetworkConfig` (2) | Identity + IP. Done before stuff that talks over the wire. |
| `Config` | `UsersGroups` (0), `SetPasswords` (1), `SshAuthorizedKeys` (2), `WriteFiles` (3), `Runcmd` (4) | Host configuration. The bulk of the cloud-config schema lives here. |
| `Final` | `ScriptsUser` (0) | User-supplied scripts; runs after everything else has settled. |

Modules declare their stage and order via the `[Stage(...)]` attribute.
Order within a stage is the integer from the attribute, ties broken by
type name.

## Run order

`StageRunner.RunAsync(ct)` walks the stages top-to-bottom. Per stage:

1. Emit `StageStarted`.
2. Resolve user-data once (lazy — happens on first stage that isn't
   `Local`).
3. For each module: check the semaphore; skip if already satisfied;
   otherwise call `ApplyAsync`.
4. On `Completed` / `RebootRequested`, write the semaphore. On `Failed`
   no semaphore is written — the module re-runs next pass.
5. Emit `StageCompleted`.

After Final completes successfully:
- Emit `ProvisioningCompleted`.
- Call the datasource's `OnCompletedAsync` cleanup hook (Azure deletes
  `CustomData.bin` here; NoCloud / ConfigDrive / KVP are no-ops). See
  [RFC 0005](../../rfcs/0005-datasource-cleanup-hook.md).

## Running a single stage

```powershell
egs-service run --stage network
egs-service run --stage config
```

User-data is resolved lazily so a `--stage local` call doesn't pay the
parse cost. `--stage network`/`config`/`final` resolves user-data on
entry — the Config and Final modules depend on it.

## Reboot-and-continue

A module that returns `ModuleOutcome.Reboot(...)` (e.g.
`SetHostname` when Windows says rename-is-pending, or `Runcmd` /
`ScriptsUser` when an entry exited 1003) suspends the run. The runner:

1. Writes the module's semaphore (so this module is not re-evaluated
   after the reboot).
2. Returns `StageRunOutcome.RebootRequested`. The CLI translates this
   into `shutdown.exe /r /t 5` unless `--dry-run` was given.

On the next boot, the agent's per-instance semaphores survive; the
agent skips the already-completed modules and resumes from the next
one.

## Cloud-init mapping

| Cloud-init stage | Agent stage |
| --- | --- |
| `init-local` | `Local` |
| `init` (network) | `Network` |
| `modules:config` | `Config` |
| `modules:final` | `Final` |

The four-stage shape and the names match by intent. Cloud-init's
explicit `cloud_init_modules` / `cloud_config_modules` /
`cloud_final_modules` config-driven module lists are not exposed in
v1 — the agent's module set is fixed. See
[RFC 0009](../../rfcs/0009-module-list-split.md) for the deferred
mechanism.
