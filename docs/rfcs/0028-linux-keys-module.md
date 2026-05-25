# RFC 0028 — Acknowledged-but-no-op cloud-config keys

Status: Implemented
Implemented in commit `<pending>`

## Problem

Cross-cloud cloud-config YAML routinely carries top-level keys with no
Windows analogue — `apt:`, `snap:`, `yum_repos:`, `packages:`,
`bootcmd:`, `phone_home:`, configuration-management bootstraps (`chef`,
`ansible`, `puppet`, `salt_minion`), and so on.

The agent's first cut treated these as truly unknown keys and emitted
Warning-level log lines per key on every catlet that shipped a
Linux-flavoured cloud-config. That was noisy and miscategorised: an
`apt:` block in a cross-cloud YAML is not a typo or a mistake — it's
the operator deliberately writing one cloud-config that targets both
Linux and Windows guests. Warning was the wrong level.

## What cloud-init does

Cloud-init's runtime calls `validate_cloudconfig_schema()` against a
JSON schema. Schema additions for new keys go directly into
`cloudinit/config/schema.py`'s `additionalProperties: false` exception
list — i.e. unknown keys are detected via *schema membership*, not via
a hardcoded "known Linux" list. Unknown keys log at WARNING; known
keys handled by no module that ran also don't emit a special signal —
they're just silently accepted because they ARE on the schema.

Reference: `cloudinit/config/schema.py` `validate_cloudconfig_schema`
+ each module's individual JSON schema fragment.

## What ships

Tiered "what level do I log this at?" classification, all decided
inside `CloudConfigSerializer`:

1. **Implemented modules** (Growpart, NtpClient, Timezone, SetLocale,
   Licensing, etc.) — read their cloud-config keys directly. The
   serializer emits nothing for them; silence is the right signal for
   "the module took it from here."
2. **Acknowledged-but-no-op keys** (Linux-only + deferred) — listed as
   schema fields (`object?` for polymorphic shapes, typed scalars for
   booleans / strings) on `CloudConfig`. `CloudConfigSerializer.Deserialize`
   walks the parsed POCO against a built-in `AcknowledgedKeys` table
   and logs each non-null entry at **Info** with a short explanation
   ("Linux APT package source configuration; no-op on Windows").
3. **Truly unknown keys** (typos, vendor extensions) — caught by the
   `CloudConfigYamlSerializer` unknown-key callback and logged at
   **Warning**, also inside `CloudConfigSerializer.Deserialize`.

Both halves of the "what did the YAML carry that the agent did not act
on?" inventory live in `CloudConfigSerializer`. Operators reading the
log see the full picture from one place.

### Why this is NOT a module

The first implementation packaged tier 2 as a fake `LinuxKeysModule`
registered in the stage runner. That was a developer-convenience
choice — `IModule` already had `ResolvedUserData` and `ILogger<T>`
plumbed, so registering the observation as a module took the fewest
new lines. Misleading on two axes:

- **Operator-facing**: a "module" in cloud-init's vocabulary is a
  thing that *does* something. `LinuxKeys` did not. `disabledModules:
  [LinuxKeys]` looked like a control over Linux-key handling; actually
  it was a control over the *paper trail*, not behaviour.
- **Developer-facing**: registered with `Stage.Network, Order = 0,
  Frequency = PerInstance`, it wrote per-instance semaphores in
  `state.json` for what was pure logging. Dead weight.

Folded into `CloudConfigSerializer` instead — observation lives at
the *parse* layer, where unknown-key Warning emission also lives.
Modules stay reserved for things that act.

### Why schema fields and not a hardcoded "known Linux" set?

Two reasons:
- A schema field documents itself — anyone reading `CloudConfig.Apt`
  understands the agent's contract. A hidden static list inside the
  serializer is invisible to consumers.
- The polymorphic shape needs a place to land. YamlDotNet deserializes
  a mapping into `Dictionary<object, object>` when the target is
  `object?`. That's fine — we don't read the contents, just check
  non-null in `CloudConfigSerializer.AcknowledgedKeys`'s presence
  predicates.

### Acknowledged keys (initial set)

Linux package management — `apt`, `apt_pipelining`, `packages`,
`package_update`, `package_upgrade`, `package_reboot_if_required`,
`snap`, `yum_repos`, `yum_repo_dir`.

Linux disk / filesystem / mount — `disk_setup`, `fs_setup`, `mounts`.

Linux network / hosts — `manage_etc_hosts`, `manage_resolv_conf`,
`resolv_conf`.

Deferred cloud-init modules — `bootcmd`, `phone_home`,
`final_message`, `ca_certs`, `disable_root`, `disable_root_opts`.

Configuration-management bootstraps (future modules) — `chef`,
`ansible`, `puppet`, `salt_minion`.

When one of those keys becomes an implemented module on Windows
(`power_state` is the precedent — see RFC 0024), it is **removed**
from the acknowledged-key table in `CloudConfigSerializer`. The
module's own logging takes over.

## What deliberately stays at Warning

A genuinely unknown key — one not in the implemented-module set AND
not in this acknowledged set — still logs at Warning via the
`CloudConfigYamlSerializer.Deserialize(onUnknownKey)` callback that
`CloudConfigSerializer` passes in. That's the path for typos
(`hsotname:` instead of `hostname:`) and undocumented vendor
extensions: the operator must see those because they likely indicate
a problem with the YAML.

## What changed from the original implementation

- **`LinuxKeysModule` deleted.** Replaced with an `AcknowledgedKeys`
  table inside `CloudConfigSerializer.Deserialize`. No more dead
  per-instance semaphore in `state.json`; no more misleading entry in
  the module list.
- **`LinuxKeysModuleTests` deleted.** Replaced by
  `CloudConfigSerializerTests` covering the same scenarios at the new
  home: single key, multiple keys, no keys (no Info noise), explicit
  `false` boolean, truly unknown key (Warning), implemented-module key
  (silence).
- **Stage attribute gone.** The acknowledged-key inventory runs at
  YAML deserialize time, not at any stage. It still fires before any
  module sees the parsed `CloudConfig` because parsing happens during
  user-data resolution, which happens before stage modules dispatch.

## Cross-references

- [Cross-cloud scope](../../) memory — the principle that drove the
  schema-extension instead of a hardcoded list.
- [RFC 0024](0024-power-state-module.md) — example of a key
  transitioning out of the acknowledged set into an implemented module.
- [RFC 0029](0029-secure-config-bootstrap-handshake.md) — when
  `secure_bootstrap` ships on Windows it should be added to the
  acknowledged set so cross-OS YAML does not warn-spam on either side.
