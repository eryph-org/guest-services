# Bug 0001 — Scripts after exit-1003 reboot are silently dropped

Status: Fixed
Discovered: 2026-05-22
Fixed: 2026-05-22 (commit `d0c476e`)
Service version observed: `0.3.1-provisioning-agent.37+Sha.9aab6d06d0926fa583c0c9a8198f385763575c5d`
Severity: High (silent partial provisioning, `state.json` reports success)

## Fix summary

1. `ISemaphoreStore.ReadOutcomeAsync` distinguishes `"completed"` from
   `"reboot-requested"`; `StageRunner` skips only on `"completed"` and
   re-enters on `"reboot-requested"`.
2. `ProvisioningState` gains `PendingHandlers` (reboot-pending) and
   `ModuleRebootCounts` (loop-safety). A reboot-pending handler is NOT
   advertised in `CompletedHandlers` — eryph-genes Pester can tell "all
   done" from "stuck pending resume".
3. `StageRunner` enforces a per-module reboot cap (3) — a module that
   keeps requesting reboot without progress fails the run instead of
   looping forever.
4. `ScriptsUserModule` gained an `IScriptCheckpointStore` dependency
   (file-backed `%ProgramData%\eryph\provisioning\instance\<id>\scripts.json`).
   The module persists `(ordinal, body-hash)` per executed script;
   resume after reboot skips already-executed entries. A per-script
   reboot quota (`MaxRebootsPerScript = 2`) provides the final
   loop-safety net.

Regression tests:
- `test/.../Stages/StageRunnerRebootResumeTests.cs` — module re-enters
  after reboot; `PendingHandlers` vs `CompletedHandlers`; per-module cap.
- `test/.../Modules/ScriptsUserModuleCheckpointTests.cs` — resume skips
  already-executed scripts; body-hash invalidates on edit; per-script
  quota.
- `test/.../Modules/FileScriptCheckpointStoreTests.cs` — atomic write,
  corrupt-file tolerance, reset.

## Summary

When a user-data script returns `1003` (`ERROR_SUCCESS_REBOOT_REQUIRED`),
`ScriptsUserModule` correctly returns `ModuleOutcome.Reboot(...)` and the
service reboots the VM. After the reboot, however, `ScriptsUserModule` is
skipped entirely on resume — its semaphore is already on disk from the
pre-reboot run. The remaining scripts in the user-data queue (everything
that was declared after the script that requested the reboot) are never
written to `scripts/per-instance` and never executed.

`state.json` ends up marking provisioning as fully successful (all four
stages, all eight modules in `completedHandlers`, no failure-named
property anywhere), which makes this look like a green run.

This contradicts the design in [RFC 0007 §"Decisions"][rfc0007]:

> **Exit 1003 → `RebootRequested` outcome.** Mid-stage abort; resume on
> next boot via the standard reboot-and-continue mechanism. (Cloud-init
> doesn't do this; we do because cbi-compat matters for eryph genes —
> `rearm-evaluation.ps1` uses 1003.)

## Reproduction

Deploy any catlet whose fodder includes a gene that requires a reboot
(e.g. `dbosoft/hyperv:install` returns 1003) and additional fodder
declared **after** it. The minimal repro used during discovery:

```yaml
name: devboxtest
parent: dbosoft/winsrv2022-standard/<recent>

capabilities:
  - nested_virtualization
cpu: { count: 4 }
memory: { startup: 16384, minimum: 8192 }
drives:
  - { name: sdb, size: 100 }

fodder:
  - source: gene:dbosoft/chocolatey:install
  - source: gene:dbosoft/windevdrive:configure
    variables:
      - { name: devdrive_name,   value: "sdb" }
      - { name: devdrive_letter, value: "E"   }
      - { name: devdrive_label,  value: "DevDrive" }
  - source: gene:dbosoft/hyperv:install         # exits 1003
  - source: gene:dbosoft/powershell:win-install # NEVER RUNS
  - name: install-dev-tools                     # NEVER RUNS
    type: shellscript
    filename: install-devtools.ps1
    content: |
      choco install microsoft-windows-terminal -y --no-progress
      choco install git -y --no-progress
      choco install nodejs-lts -y --no-progress
      choco install gh -y --no-progress
  - source: gene:dbosoft/vscode:win-vscode      # NEVER RUNS
  - name: configure-git                         # NEVER RUNS
    type: shellscript
    filename: configure-git.ps1
    content: |
      git config --system init.defaultBranch main
```

## Evidence captured from the affected VM

`C:\ProgramData\eryph\provisioning\state.json`:

```json
{
  "instanceId": "0be36b76-08a4-441b-9fbe-a5adcf9ad833",
  "completedStages":   ["Local","Network","Config","Final"],
  "completedHandlers": [
    "Eryph.GuestServices.Provisioning.Modules.SetHostnameModule",
    "Eryph.GuestServices.Provisioning.Modules.ApplyNetworkConfigModule",
    "Eryph.GuestServices.Provisioning.Modules.UsersGroupsModule",
    "Eryph.GuestServices.Provisioning.Modules.SetPasswordsModule",
    "Eryph.GuestServices.Provisioning.Modules.SshAuthorizedKeysModule",
    "Eryph.GuestServices.Provisioning.Modules.WriteFilesModule",
    "Eryph.GuestServices.Provisioning.Modules.RuncmdModule",
    "Eryph.GuestServices.Provisioning.Modules.ScriptsUserModule"
  ],
  "rebootCount": 1,
  "startedAt":   "2026-05-22T19:47:38+00:00",
  "lastUpdated": "2026-05-22T17:49:36+00:00"
}
```

`C:\ProgramData\eryph\provisioning\scripts\per-instance\`:

```
001-rearm-evaluation.ps1     (inherited from base catlet starter-food)
002-install-chocolatey.ps1
003-configure-devdrive.ps1
004-hyperv.ps1
```

`C:\ProgramData\eryph\provisioning\logs\004-hyperv.ps1.log`:

```
WARNING: Hyper-V PowerShell is not installed. Installing feature...
WARNING: Hyper-V-Tools are not installed. Installing feature...
WARNING: Hyper-V is not installed. Installing feature...
WARNING: You must restart this server to finish the installation process.
WARNING: A restart is required to complete the installation of Hyper-V features.
exit-code: 1003
```

Tools that should have been installed by scripts 005+ but are absent:
`pwsh`, `git`, `node`, `gh`, `code`. `E:\source\repos` was created by the
configure-git script's tail — wait, no: that script never ran either; the
empty `E:\source\repos` was created by `windevdrive:configure`. Git
system-wide config (`init.defaultBranch=main`, `core.autocrlf=true`,
`credential.helper=manager`) is also unset, confirming the
`configure-git` script never executed.

## Root cause

`Stages\StageRunner.cs` lines 183–188 (current `main`):

```csharp
case ModuleOutcome.RebootRequested reboot:
    // Write the marker BEFORE returning — otherwise the
    // post-reboot run would re-execute the module and we
    // would loop forever.
    await semaphoreStore.WriteAsync(
        moduleKey, frequency, data.InstanceId,
        "reboot-requested", cancellationToken)
        .ConfigureAwait(false);
    ...
    return new StageRunOutcome.RebootRequested(reboot.Reason);
```

…combined with the gate at line 129:

```csharp
if (await semaphoreStore.ExistsAsync(moduleKey, frequency, data.InstanceId, ...))
{
    logger.LogInformation("Skipping module {Module} ({Frequency}); semaphore already present", ...);
    continue;
}
```

The semaphore *value* (`"reboot-requested"` vs `"completed"`) is written
but never read. After reboot, `ExistsAsync` returns true → the module is
skipped → its for-loop over `userData.Scripts` (which always starts at
index 0) never gets a chance to resume.

`ScriptsUserModule.cs` does **not** persist any "next script index" /
checkpoint either. Even if the StageRunner re-ran it, the module would
re-execute scripts 001–004 from scratch — which would loop on 1003
forever.

So the current code shape is structurally incapable of
reboot-and-continue for user scripts: the StageRunner gate kills the
module before it can resume, and the module wouldn't know where to
resume from anyway.

`completedHandlers` in `state.json` is populated from the same source
(any semaphore for the module). That's why a half-finished
ScriptsUserModule appears as "completed" in `state.json`.

## What "right" looks like

Two pieces have to change:

1. **StageRunner must distinguish semaphore values.**
   On entry, read the semaphore value. If it is `"completed"`, skip.
   If it is `"reboot-requested"`, re-enter the module — and *only* that
   module — to give it a chance to resume. Do not loop forever; cap on
   `rebootCount` or refuse to re-enter the same module twice without
   making progress (see "Loop-safety" below).

2. **ScriptsUserModule must persist per-script checkpoint.**
   Track which scripts have already run (e.g. write a sibling state file
   `scripts.json` listing executed-ordinals next to `state.json`, or
   drop a `001-foo.ps1.done` marker next to each staged script). On
   resume, skip the executed ones, run from the next un-executed
   ordinal. When the queue is exhausted, switch the module's semaphore
   value from `"reboot-requested"` to `"completed"` and reflect that in
   `state.json.completedHandlers`.

Bonus: `state.json` schema should grow a `pendingHandlers` (or similar)
array so a consumer can tell the difference between "all handlers ran to
completion" and "one handler requested reboot and is waiting to
resume." Right now both states look identical from the outside, which
is what made this bug silent. The Pester assertion we added in
`eryph-genes` (`tests/...Provisioning State (egs-service)`) cannot
detect it either — it would happily green-tick a half-run image.

## Loop-safety

A naïve "always re-run on `reboot-requested`" can loop forever if a
script keeps returning 1003 (broken installer, or a script that races
its own prerequisites). Guard with one or more of:

- **Cap per-module reboot count** — e.g. ≤ 3 reboots requested by a
  single module per instance, then fail the module.
- **No-progress detection** — if the module re-enters and its
  checkpoint index does not advance past the script that requested the
  reboot, treat as a hard failure.
- **Per-script reboot quota** — combined with the per-script
  checkpoint: each `(ordinal, body-hash)` may only request reboot once.

The cap belongs in StageRunner (since it's the only place that knows
about `rebootCount`); the per-script reboot quota belongs in
`ScriptsUserModule` since only the module knows the script identity.

## Impact

- **Any gene that legitimately reboots blocks everything declared after
  it.** `hyperv:install`, `wsl:install`, anything that touches the
  Windows servicing stack are all affected.
- **`state.json` is unreliable as a provisioning-success signal** in
  the presence of reboot-requesting scripts. Consumers (and the new
  Pester assertions in eryph-genes / `Test-PackedBaseCatlet.Tests.ps1`)
  will mark provisioning as successful when it actually stopped early.
- **No error log surfaces.** The `004-hyperv.ps1.log` records exit-1003
  faithfully; nothing else in `state.json`, the Application event log,
  or the service log records that scripts 005+ were dropped.

## Suggested next steps

1. Reproduce in a unit/integration test (mock an IModule that returns
   `Reboot` on first call and `Completed` on second). Today the module
   never gets the second call.
2. Extend `IStateStore` / `state.json` schema to record per-handler
   status (Completed / RebootPending / Failed) rather than relying on
   semaphore existence alone.
3. Add a per-script checkpoint inside `ScriptsUserModule` (durable —
   the VM may reboot between scripts).
4. Tighten the eryph-genes Pester assertion to read the new
   per-handler status field once it's in place.

[rfc0007]: ../rfcs/0007-scripts-per-frequency-edge-cases.md
