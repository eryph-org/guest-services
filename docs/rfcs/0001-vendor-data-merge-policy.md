# RFC 0001 — Vendor-data merge policy

Status: Implemented (option 2 — deep-merge per cloud-init default; vendor-data resolved through the same pipeline and merged with user-data winning on conflict)

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

## Tentative direction (superseded by Decision below)

**(3) for v1**, **(2) for v2 when a use case appears**. Rationale: deferring is cheap (the chassis already accepts vendor-data; the discard happens in `UserDataPipeline`). Picking the wrong merge policy without a real-world example to validate against is risky.

## Decision

**Option (2) — deep-merge per cloud-init default — was chosen and implemented.** The "wait for a real use case" condition was met when the OpenStack datasource landed: OpenStack ships `vendor_data.json`, so vendor-data became a live, producer-backed payload rather than a hypothetical. Discarding it (option 3) would have silently dropped provider-supplied cloud-config.

How it works today:

- **Same pipeline, lower priority.** `StageRunner` resolves vendor-data through the *same* `UserDataPipeline` as user-data, then calls `ResolvedUserData.Combine(resolvedVendor, resolvedUser)` — vendor-data is the lower-priority source, user-data the higher.
- **Cloud-config merge** is `CloudConfig.CloudConfigMerge.Merge`: scalars → user-data (right) wins when non-null; lists → concatenated vendor-first; dicts → deep-merged; `users` / `groups` / `chpasswd` users → merged by `Name` with the user-data entry winning on conflict. This matches cloud-init's default `cloud_init_merge` semantics.
- **Scripts and boothooks** are concatenated vendor-first, so vendor-data entries run before user-data entries.
- **Producers:** OpenStack `vendor_data.json` (with cloud-init's `convert_vendordata` extraction cloned in `OpenStackMetadataReader.ConvertVendorData`) and NoCloud's `vendor-data` file. For datasources that supply none, `GetVendorDataBytes()` returns empty and the merge is a no-op.
- **Tests:** `ResolvedUserDataCombineTests` (`User_data_wins_over_vendor_data_on_conflict`, `Vendor_data_fills_fields_user_data_omits`).

Implemented in commit `679cb3e` (alongside the OpenStack datasource that motivated it).

## Open questions (resolved)

- *Does Azure's `ovf-env.xml` count as vendor-data once parsed by Azure PA?* — **No.** `AzureDataSource` sets `VendorData = null`; the platform agent has already applied that config, so there is nothing for us to merge. Confirms the original guess.
- *Should an explicit `cloud_init_merge` directive in user-data be honored, or fixed per RFC outcome?* — **Fixed for vendor-data precedence; the general question moved to [RFC 0032](0032-cloud-config-merge-model.md).** Today the merge strategy is not user-configurable (no `cloud_init_merge` / `merge_how` handling). Whether eryph should add per-fragment merge control across its multi-source cloud-config stacking is a broader decision than vendor-data, and is owned by RFC 0032.
