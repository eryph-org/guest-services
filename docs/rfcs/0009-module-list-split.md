# RFC 0009 — Module-list split (`cloud_init_modules` / `cloud_config_modules` / `cloud_final_modules`)

Status: Draft

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

## Tentative direction

**Build now with discovery + attribute (current model). Add config override later.** The `egs-provisioning.json` settings file can grow per-stage allowlists / denylists:

```json
{
  "stages": {
    "Network": { "enabled_modules": [ "SetHostnameModule" ] },
    "Config":  { "enabled_modules": "*" },
    "Final":   { "disabled_modules": [ "PhoneHomeModule" ] }
  }
}
```

For v1, all discovered modules run in their declared stage. No config required.

## Open questions

- Should disabled modules log a Debug event when skipped, or be silent?
- Per-platform module enablement: e.g., on Azure, the `SetHostnameModule` is redundant (PA did it). Encode this in the datasource (`Hostname = null`) rather than per-platform module config. Confirm.
