# RFC 0032 — Cloud-config merge model & merge control

Status: Accepted

## Problem

Eryph rarely provisions from a single cloud-config. A catlet is composed
from multiple fodder/gene fragments, and on top of that the agent stacks
cloud-config from several independent sources at runtime. They all collapse
into one effective cloud-config through a single deep-merge, but **how** they
combine — the precedence order and the per-key strategy — has never been
written down as a decision. It lives only as code and source-gen attributes.

Worse, the merge is **hardcoded**: a later fragment can only ever *append* to
a list, never *replace* it. There is no way for a fragment to say "drop
whatever `runcmd` / `packages` / `users` accumulated and use mine instead."
cloud-init solves this with per-fragment merge-control directives
(`merge_how` / `merge_type`, historically `cloud_init_merge`). Eryph has no
equivalent and no recorded decision on whether it should.

This RFC captures the existing model as an agreed design and decides what to
do about merge control.

## What cloud-init does

Cloud-init merges cloud-config parts in order and lets any part override the
default strategy:

- **Default strategy** (`list(append)+dict(no_replace,recurse_list)+str()`):
  scalars — later wins; lists — concatenated; dicts — deep-merged, novel keys
  added, existing keys recursed.
- **Per-fragment control:** a part may carry `merge_how:` (a.k.a.
  `merge_type:`) selecting `list`/`dict`/`str` mergers with modifiers like
  `append`, `no_replace`, `replace`, `recurse_dict`, `recurse_list`,
  `recurse_array`. A part can thus opt into "replace this list" rather than
  the default append.
- A handful of keys have bespoke merge semantics regardless of strategy
  (`users`, `runcmd`, write_files by path, etc.).

See https://docs.cloud-init.io/en/latest/reference/merging.html.

## What cloudbase-init does

No merge model. User-data only, single payload, last-writer semantics. Nothing
to mirror.

## Eryph context — the sources that stack

All of these funnel through one function, `CloudConfig.CloudConfigMerge.Merge(left, right)`:

| Source | Where | Precedence (who wins on conflict) |
|---|---|---|
| Multipart MIME parts (multiple `#cloud-config` parts) | `UserDataResolutionContext.MergeCloudConfig` | later part over earlier |
| `#include` URL fragments | same, via recursive dispatch | later over earlier |
| vendor-data vs user-data | `ResolvedUserData.Combine` (RFC 0001) | user-data over vendor-data |
| secure-config bootstrap fragment | same merge path (RFC 0029) | fragment over base |

The **per-property strategy** is source-generated from `[MergeBehavior(MergeKind)]`
attributes on the model (`MergeKind.cs`):

- `RightWins` — scalars (`string?`/`bool?`/numeric/enum/`object?`).
- `Concat` — non-keyed lists (`runcmd`, `packages`, `ssh_authorized_keys`, …),
  left-then-right.
- `DeepMerge` — nested records, field by field.
- `KeyedByName` — `users` / `groups` / `chpasswd.users`, matched by `Name`,
  later entry deep-merged onto earlier.
- `Auto` — inferred from the declared type.

This is a faithful clone of cloud-init's *default* strategy. What it has no
representation for is the *override* — there is no `merge_how` parsing, and
`MergeKind` is fixed at the model level, identical for every source.

Why this matters for eryph specifically: fodder/gene composition is additive
by design, so "append" is the right default — but it is also the *only*
option today. An author who wants a fragment to take authoritative control of
a list (e.g. a hardening gene that must define the *exact* `users` set, or a
fragment that replaces an inherited `runcmd` rather than tacking onto it)
cannot express that. The limitation is invisible until someone hits it, and
then there is no escape hatch.

## Options for merge control

1. **Keep it fixed (status quo).** Append-only, no per-fragment control.
   Simplest; documents the current behaviour and closes the question. Cost:
   no way to replace inherited lists; authors work around it by not splitting
   fragments, which defeats gene composition.

2. **Support cloud-init `merge_how` / `merge_type` verbatim.** Parse the
   directive per part and switch mergers accordingly. Maximum cloud-init
   parity; familiar to anyone who has used it. Cost: the directive is fiddly
   and underspecified at the edges, applies per-part (not per-key), and the
   source-generated merger would need a runtime-selectable strategy it does
   not currently have.

3. **Eryph-native, per-key control directive.** A small top-level key (e.g.
   `eryph_merge:` / `merge:`) that names keys to `replace` instead of the
   default. Narrower and clearer than cloud-init's part-level switches, fits
   the source-gen model (a replace-set consulted per property), and targets
   the actual need (replace a specific inherited list) without importing the
   whole `merge_how` grammar. Cost: not cloud-init-compatible syntax;
   authors must learn it.

4. **(2) for compatibility + (3) for ergonomics.** Honor `merge_how` when
   present for parity, expose the native directive as the recommended path.
   Most capable; most surface area.

## Decision

- **Ratify the existing default model** (precedence chain + `MergeKind`
  taxonomy above) as the agreed design.
- **Adopt option (2): cloud-init `merge_how` / `merge_type`.** A fragment
  controls how it merges onto the accumulated config with cloud-init's own
  part-level directive — both the string form
  (`merge_how: "list(append)+dict(no_replace,recurse_list)+str()"`) and the
  structured list form (`- name: list` / `settings: [...]`). This keeps eryph
  on its cloud-init-parity north star and means fodder authored against
  cloud-init's documented behaviour merges identically here.
- **Default (no directive) = cloud-init's default merger**, which is the
  behaviour already implemented (lists append/concat, dicts deep-merge, scalars
  later-wins). Existing fodder is therefore unaffected.
- **Supported in both merge sites:** the host-side pre-merge library
  (`CloudConfigComposer`) and the guest provisioning pipeline
  (`UserDataResolutionContext` cloud-config accumulation). The directive is
  read per fragment from its raw YAML and applied as that fragment merges onto
  the accumulator.

### Semantics implemented

The directive is parsed into a `CloudInitMergeOptions` (list / dict / str
actions) threaded through the source-generated merge:

- **list:** `append` (default), `prepend`, `replace`, `no_replace`.
- **dict:** deep-merge/recurse (default), `replace` (incoming value replaces).
- **str:** `replace` (default). `str(append)` is parsed but not applied to
  typed scalars — concatenating distinct typed scalars (e.g. two hostnames)
  is not meaningful; revisit if a real use case appears.
- **`KeyedByName`** (`users` / `groups` / `chpasswd`): honours the same list
  actions as plain lists — `replace` swaps the whole list, `no_replace` keeps
  the accumulated one, `prepend`/`append` set ordering — while same-named
  entries are deep-merged (incoming wins), matching cloud-init's effective
  result.

One deliberate simplification: `dict(no_replace)` maps to the default recurse
merger, in which a conflicting key takes the incoming (right) value — the
behaviour eryph already had. cloud-init's `no_replace` technically means the
*accumulated* value is kept on a key conflict. Wholesale `dict(replace)` is
supported; left-wins-on-key (true `no_replace`) is not modelled, because no
fodder needs it yet. Revisit if one does.

Settings the model does not represent (e.g. `recurse_array`, `allow_delete`)
are parsed permissively and ignored rather than rejected.

## Resolved questions

- **Granularity:** part-level (cloud-init's model). A fragment's directive
  governs every key it carries as it merges onto the accumulator.
- **Where the directive lives:** on the fragment that declares it, read from
  that fragment's raw YAML and applied at the point it merges. A fragment
  arriving via multipart or `#include` carries its own directive.
- **Interaction with `KeyedByName`:** all four list actions apply —
  `list(replace)` drops the whole list, `no_replace` keeps the accumulated
  one, `prepend`/`append` set ordering — with same-named entries deep-merged.
- **Default behaviour is non-destructive**, so no special logging is required;
  a `replace` is an explicit, author-chosen directive.

## Implementation — merge as a library feature for eryph

The merge model above is currently reachable only from inside the guest agent.
Eryph wants to merge fodder **before** the guest boots — collapse the several
`type: cloud-config` fodder fragments of a catlet into one cloud-config and
write that single document to the NoCloud seed disk, rather than shipping a
pile of fragments for cloud-init to reconcile at runtime. That makes the merge
a build-time, host-side concern, so it must be exposed as a referenceable
library.

### What already exists

- `CloudConfig.CloudConfigMerge.Merge(CloudConfig left, CloudConfig right)` is
  **public** and is exactly the source-generated deep merge described above.
  Folding an ordered list of fragments left-to-right *is* the pre-merge.
- `CloudConfig.Yaml.CloudConfigYamlSerializer.Deserialize(string)` is
  **public** (YAML → model, with `onUnknownKey` warnings and
  `InvalidConfigException` on malformed input). `EryphFodderSmokeTests` already
  exercises this against real genepool `cloud-config` fodder.

### Gaps to close

1. **Serialization (model → YAML).** The Yaml library only deserializes — the
   guest agent consumes cloud-config and never emits it. The pre-merge use case
   is the inverse, so a `Serialize(CloudConfig)` is needed. It MUST reuse the
   same converter/alias/naming configuration as the deserializer (the
   `WithAttributeOverride` rules, `BoolOrString`, `write_files` permissions,
   runcmd shapes, `ssh_deletekeys`/`locale_configfile` aliases, snake_case),
   emit the `#cloud-config` header, and omit null/empty members. Round-trip
   fidelity (parse → serialize → parse is stable) is the main risk and must be
   pinned with tests. Several existing `IYamlTypeConverter`s implement only the
   read path and will need their write path filled in.

2. **Packaging.** Neither `Eryph.GuestServices.CloudConfig` nor
   `.CloudConfig.Yaml` is `IsPackable` today (only Core/Sockets/HvDataExchange
   are). Both opt in with an `IsPackable`/`PackageId` so eryph can take a
   `PackageReference`. The source-generated `Merge` is baked into the
   `CloudConfig` assembly, so the analyzer/source-gen project does not ship.

### Proposed surface

A thin facade in the Yaml library (it needs both the model and the serializer):

```csharp
namespace Eryph.GuestServices.CloudConfig.Yaml;

public static class CloudConfigComposer
{
    // Parse each fragment, fold left→right via CloudConfigMerge.Merge,
    // serialize the single result as a #cloud-config document.
    public static string Merge(IEnumerable<string> cloudConfigFragments,
                               Action<string>? onUnknownKey = null);

    // Same, stopping at the merged model (for callers that validate or
    // post-process before serialising).
    public static CloudConfig MergeToModel(IEnumerable<string> cloudConfigFragments,
                                           Action<string>? onUnknownKey = null);
}
```

Fragment order is precedence: a later fragment wins on scalars and concatenates
onto earlier lists, exactly per the model above. Callers may run
`CloudConfigValidations` on the merged result to fail fast before writing the
seed disk.

### Relationship to the merge-control decision

This exposure is **decision-neutral** with respect to the open
`replace`/`merge_how` question. The facade surfaces only the *default* append
merge, which already exists and is stable. A future per-key `replace` directive
(or `merge_how` support) extends the same fold without changing the
`CloudConfigComposer` API, so building the library exposure now does not
pre-commit the control design. The exposure can therefore land while this RFC's
*control* sections remain Draft; only the merge-control directive itself waits
on a concrete fragment that needs it.

### Scope boundary

The facade collapses cloud-config fragments only. Boothooks, `x-shellscript`
parts, and multipart structure are not merged — eryph decides which fragments
are cloud-config and feeds those in. Non-cloud-config payloads continue to ride
as their own user-data parts.

## Authoring note

This RFC supersedes the narrow "merge is fixed" disposition recorded in
RFC 0001's resolved open questions: that answer was reasoned about vendor-data
precedence only. The general merge-control decision is owned here.
