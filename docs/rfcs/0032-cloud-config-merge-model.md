# RFC 0032 — Cloud-config merge model & merge control

Status: Draft

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

## Tentative direction

- **Ratify the existing default model** (precedence chain + `MergeKind`
  taxonomy above) as the agreed design — independent of the control question.
  This is descriptive, not a behaviour change.
- **Lean toward (3)** for merge control: an eryph-native per-key `replace`
  directive, because the concrete need is "let a fragment own a list," it
  composes cleanly with the source-generated merger, and it avoids committing
  to cloud-init's full part-level `merge_how` semantics before we have a real
  fragment that needs them. Revisit (2) if cloud-init-syntax compatibility
  turns out to matter for imported user-data.
- **No implementation until a fragment actually needs replace semantics.** The
  default append behaviour is correct for the composition we do today; this
  RFC exists so the decision is made deliberately when the need arrives rather
  than reactively.

## Open questions

- **Granularity:** per-key (option 3) or per-part (cloud-init's model)? Gene
  composition is key-oriented, which argues per-key — but a fragment that
  wants to replace *everything* it touches would prefer a part-level switch.
- **Where does the directive live** when a fragment is itself multipart or
  arrives via `#include`? Presumably on the fragment that declares it, applied
  as that fragment merges onto the accumulator.
- **Interaction with `KeyedByName`** (`users`/`groups`): does `replace` drop
  the whole list, or replace only same-named entries? Cloud-init keeps the
  by-name semantics; we likely should too.
- **Validation/observability:** a replace is destructive — should it be logged
  at Info so a surprising "where did my inherited `runcmd` go" is traceable?
- **Does eryph's fodder layer** (outside the guest agent) already resolve some
  of this before it reaches user-data, narrowing what the agent must handle?

## Authoring note

This RFC supersedes the narrow "merge is fixed" disposition recorded in
RFC 0001's resolved open questions: that answer was reasoned about vendor-data
precedence only. The general merge-control decision is owned here.
