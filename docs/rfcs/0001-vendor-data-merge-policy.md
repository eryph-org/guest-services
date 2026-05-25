# RFC 0001 — Vendor-data merge policy

Status: Draft

## Problem

Cloud platforms can ship two userdata-shaped payloads: **user-data** (end-user supplied) and **vendor-data** (cloud-provider supplied). Both can be `#cloud-config` and both can carry overlapping directives. We need a deterministic merge policy that does the right thing across plausible eryph deployments.

## What cloud-init does

Cloud-init processes vendor-data **first**, then user-data. Conflicts: configurable via `cloud_init_merge` strategy. The default is roughly "user-data overrides on scalars, lists are concatenated, dicts are deep-merged". See https://docs.cloud-init.io/en/latest/explanation/instancedata.html#merging-user-data-sections.

## What cloudbase-init does

No vendor-data support at all. User-data only.

## Eryph context

- v1 has no use case for vendor-data — eryph-zero supplies one cloud-config blob as user-data.
- Future: Azure / AWS might emit vendor-data with platform-specific config (e.g., AWS-tuned timezone/region defaults). We don't want to be surprised.

## Options

1. **Strict user-data wins.** Any key present in user-data wipes vendor-data's value entirely. Simple, predictable.
2. **Deep-merge per cloud-init default.** List concat, dict merge, scalar override. Compatible but subtler.
3. **No merge for v1.** Vendor-data is parsed but discarded with a single Info log; we revisit when a real use case arrives.

## Tentative direction

**(3) for v1**, **(2) for v2 when a use case appears**. Rationale: deferring is cheap (the chassis already accepts vendor-data; the discard happens in `UserDataPipeline`). Picking the wrong merge policy without a real-world example to validate against is risky.

## Open questions

- Does Azure's `ovf-env.xml` contents count as vendor-data once parsed by Azure PA? (Probably not — PA already applied it; nothing for us to merge.)
- Should an explicit `cloud_init_merge` directive in user-data be honored, or fixed per RFC outcome?
