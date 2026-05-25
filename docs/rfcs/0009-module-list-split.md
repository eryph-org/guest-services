# RFC 0009 — Module-list split (`cloud_init_modules` / `cloud_config_modules` / `cloud_final_modules`)

Status: Implemented

## Problem

Cloud-init's `cloud.cfg` declares three module lists corresponding to the three module-running stages (Network, Config, Final):

```yaml
cloud_init_modules:
  - disk_setup
  - growpart
  - resizefs
  - set_hostname
  - ...
cloud_config_modules:
  - ssh
  - set_passwords
  - users_groups
  - write_files
  - ...
cloud_final_modules:
  - runcmd
  - scripts_user
  - power_state_change
  - phone_home
  - ...
```

Our model: modules declare their stage via `[Stage(Stage.Xxx)]` attribute. Equivalent in mechanism, but cloud-init's three-list config gives the operator explicit control over what runs in each phase.

## Should we mirror that?

Arguments for (cloud-init parity):
- Operators can disable specific modules without code changes (`egs-provisioning.json` lists modules per stage).
- Compatibility with cloud-init mental model.

Arguments against:
- Eryph delivers a fixed set of modules; no need for operator selection.
- Adding configurable module lists in v1 is premature.

## Decisions

**Discovery + attribute is the primary model; the operator override layers on top.** Modules declare their stage via `[Stage(...)]` (current behavior, unchanged); discovery enumerates the assembly's modules per stage. `ProvisioningSettings` gains an optional `Stages` dictionary that the StageRunner consults before invoking each module.

### Schema

```json
{
  "stages": {
    "Network": { "enabledModules": [ "SetHostnameModule" ] },
    "Config":  { "disabledModules": [ "SetPasswordsModule" ] },
    "Final":   { }
  }
}
```

Both fields are optional. When the whole `Stages` object is absent (default), every discovered module runs in its declared stage — exactly the v1 behavior.

### Resolution order per stage

1. Start with all discovered modules whose `[Stage]` matches.
2. If `enabledModules` is set, narrow to only those.
3. If `disabledModules` is set, remove those.
4. Run the result.

### Name matching

- Stage keys (`"Local"` / `"Network"` / `"Config"` / `"Final"`) are case-insensitive.
- Module names match the .NET class's short name (e.g., `SetHostnameModule`), case-insensitive, and the trailing `Module` suffix is optional — both `"SetHostnameModule"` and `"SetHostname"` match.
- Unknown names log Warning (one per stage) but do not fail the run; a typo in the settings file shouldn't crash provisioning.

### Logging

- Module skipped because of allowlist / denylist: `Debug` (one line per module).
- Settings reference an unknown module name: `Warning` (one line per stage, listing all unknown names).

## Per-platform note

Per-platform module enablement (e.g. "skip `SetHostnameModule` on Azure because PA already set it") is **not** done via per-platform settings overrides. Instead, the datasource encodes that in its `DataSourceResult` — Azure already returns `Hostname` matching what PA applied, so `SetHostnameModule` becomes a same-name no-op. This keeps platform behavior in the datasource layer rather than scattering platform conditionals across settings.

## Implementation

- `ProvisioningSettings.Stages: Dictionary<string, StageSettings>?` (`StageSettings.EnabledModules`, `StageSettings.DisabledModules`).
- `StageRunner.BuildStageBuckets` calls a new `IsModuleEnabled(stage, type, settings, logger)` filter while building the per-stage map.
- 7 regression tests cover: default (no settings), enabled-only narrowing, disabled-only skipping, both set, case-insensitive stage key, Module-suffix-tolerant module name, unknown-name as Warning (not Failed).
