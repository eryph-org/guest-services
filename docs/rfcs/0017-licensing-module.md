# RFC 0017 — `license` cloud-config module

Status: Implemented
Implemented in commit `<pending>`

## Problem

Windows licensing on cloud catlets was handled by a per-gene PowerShell
script (`rearm-evaluation.ps1`) plus operator `runcmd` for product keys
/ KMS hosts. There was no cloud-config key for licensing; the gene
corpus duplicated logic; operator-visible surface was "go write
PowerShell."

This module folds the gene-side rearm script and the operator runcmd
patterns back into the agent.

## What ships

### Schema

```yaml
license:
  # Explicit overrides (highest priority).
  product_key: AAAAA-BBBBB-CCCCC-DDDDD-EEEEE
  kms_host:    "kms.example.com:1688"
  # Auto-detect against the guest's OS edition.
  set_avma: true     # default true — installs the AVMA key for this edition
  set_kms:  false    # default false — installs generic KMS-client key
  # Extras.
  activate: false    # default false — KMS clients self-activate on network
  rearm:    true     # default true — only fires when product is an evaluation
  force:    false    # default false — apply even on Azure datasource
```

POCO `LicenseConfig`. Field-naming note: top-level YAML key is `license:`
(singular) to match the natural cloud-config noun; cbi uses no cloud-config
binding here.

### Module

- `Eryph.GuestServices.Provisioning.Modules.LicensingModule`
  (`Stage.Config`, `Order = 5`, `Frequency = PerInstance`).
- Module is **always-on**: the `license:` block can be absent — defaults
  produce safe no-ops on non-Server SKUs and non-evaluation guests.
- Resolution priority: explicit `product_key` > AVMA > KMS auto.
- When `set_kms: true` and no `kms_host:` is given, `slmgr /ckms`
  clears any configured host so DNS SRV discovery takes over (corporate /
  Azure-Stack-HCI norm).
- Rearm is gated on `IsEvaluationLicenseAsync()` — runs only when the
  active SKU is a TIMEBASED_EVAL product. A successful rearm returns
  `ModuleOutcome.RebootRequested`.

### Azure datasource skip

When the active datasource is `Azure`, the **activation path** (product
key install, KMS host, `slmgr /ato`) is skipped — Azure activates Windows
itself via `kms.core.windows.net`. The **rearm path** still runs because
evaluation grace periods are an OS-level mechanism Azure does not manage.
`license.force: true` overrides the activation skip (hybrid scenarios:
corporate KMS on an Azure VM, licensing audit).

### Key tables

`Windows\Licensing\VolumeActivationKeys` — KMS and AVMA keys verified
against the Microsoft Learn AVMA and KMS reference pages:

- Server 2012R2 — Datacenter / Standard / Solution (KMS + AVMA)
- Server 2016 — Datacenter / Standard / Solution / ServerAzureCor (KMS + AVMA)
- Server 2019 — Datacenter / Standard / Solution (KMS + AVMA)
- Server 2022 — Datacenter / Standard / Datacenter:Azure Edition (KMS + AVMA)
- Server 2025 — Datacenter / Standard / Datacenter:Azure Edition (KMS + AVMA)

Edition detection: `OsVersionDetector` maps `Environment.OSVersion.Version`
build numbers to an `OsVersionFamily`. License family comes from
`SoftwareLicensingProduct` (`root\cimv2`) — the active KMS-client row's
`LicenseFamily` property — same source cbi reads.

### Defaults rationale

- `set_avma` default `true`: AVMA is host-bound, installing the key is
  inert on non-Datacenter hosts. Cost of being on is zero; benefit of being
  on activates millions of legitimate Hyper-V guests automatically.
- `rearm` default `true`: gated on `IsEvaluationLicenseAsync()` so it only
  fires when there is something to rearm. Non-eval guests skip rearm —
  no rearm slot wasted (Microsoft documents a hard limit of 5).

### IWindowsOs surface

```csharp
Task ApplyLicenseAsync(LicenseSpec spec, CancellationToken ct);
Task<RearmResult> RearmLicenseAsync(CancellationToken ct);
Task<string?> ResolveVolumeActivationKeyAsync(VolumeActivationKeyType type, CancellationToken ct);
Task<bool> IsEvaluationLicenseAsync(CancellationToken ct);
```

All five route through `cscript //nologo //b slmgr.vbs <verb>` or CIM
queries (`SoftwareLicensingProduct`). `DryRunWindowsOs` intercepts the
writes; the reads pass through.

## What changed from the original Draft — and why

- **POCO rename** `LicensingConfig` → `LicenseConfig`. Cosmetic; the
  YAML key is `license:` (singular).
- **Stage** `Stage.Final` → `Stage.Config / Order 5`. The Draft's
  argument was "box in its intended shape before activation" — fully
  configured (users, files, runcmd) before activation tries to phone
  home. Our placement runs activation BEFORE `ScriptsUser` in Final,
  but still AFTER `RuncmdModule` (Config/4). Rationale:
  1. Activation failure should surface early, not after user scripts
     have made changes a failed-activation state would corrupt.
  2. Scripts that depend on activation (Windows Update on some SKUs,
     audit tools) work correctly when scripts run on a licensed system.
  3. The Azure-skip path means most cross-cloud catlets don't hit the
     activation path here at all — timing is moot.
  4. KMS activation needs network, but network is up by the end of
     `Stage.Network` — long before Config/5.
  Both placements are defensible; the practical signal that mattered
  was "fail-fast on activation issues > scripts-prep-for-activation."
- **Field changes**:
  - **Added `SetAvma`, `SetKms`, `Force`.** Not in the Draft because the
    Draft predates the [Cross-cloud scope](../../) project decision.
    Cross-cloud means: build on Hyper-V, deploy on Azure / EC2 / OpenStack.
    Auto-detect (AVMA / KMS) is the right default for build-time;
    Azure-skip + `force` are the right runtime knobs for the deploy side.
  - **Renamed `RearmOnExpiry` → `Rearm`.** Rearm is always internally
    gated on `IsEvaluationLicenseAsync()`, so the "on expiry" qualifier
    is redundant — the field just says "rearm if eligible".
  - **Removed `KmsPort`.** Folded into `KmsHost` as `"host:port"`. One
    fewer field, one fewer validation rule, same expressiveness.
  - **Removed `Enabled`.** Block-absent IS the disable signal; explicit
    `enabled: false` was redundant once we made the module always-on
    with safe defaults (see next bullet).
- **Defaults**: `SetAvma` + `Rearm` default `true`. The Draft had all
  fields opt-in. Real-world signal: operators expect AVMA-on-Hyper-V to
  just work; they don't want to type `set_avma: true` on every catlet.
  AVMA is inert on non-Datacenter hosts and rearm only fires on eval
  SKUs, so on-by-default is safe-by-default.
- **New**: Azure-datasource skip. Not in the Draft because the Draft
  predates the deploy-side multi-cloud thinking. Windows on Azure
  activates natively via the Azure-internal KMS; our slmgr calls would
  only add noise. `force: true` overrides for hybrid scenarios.
- **New**: built-in AVMA / KMS key tables. The Draft assumed operators
  would pass explicit keys. Reviewed cbi's `productkeys.py` (which is
  the same idea but stops at Server 2016) and extended through Server
  2025 from the Microsoft Learn AVMA and KMS reference pages. Inclusion
  is publicly-documented activation material, not secrets.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — `slmgr /rearm`
  rides the reboot-and-continue contract.
- [RFC 0009](0009-module-list-split.md) — `disabledModules: [Licensing]`
  for operators that manage activation externally.
