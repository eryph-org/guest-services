# RFC 0023 — `growpart` cloud-config module

Status: Implemented
Implemented in commit `<pending>`

## Problem

A catlet whose OS disk was sized larger than the source image's
partition boots with the extra capacity sitting as unallocated space at
the end of the disk — Windows does not auto-extend partitions to fill
the disk. Operators hit this constantly: "I asked for a 64 GB disk and
`C:` is still 20 GB."

## What ships

### Schema

Mirrors cloud-init's `cc_growpart` schema rather than cbi's
config-file-only `volumes_to_extend = []`:

```yaml
growpart:
  mode: auto         # auto | off (also accepts the YAML boolean `false`)
  devices: ['/']     # default; '/' resolves to the Windows system drive
```

`devices` accepts:

- `/` — the system drive (`%SystemDrive%`, normally `C:`).
- A drive letter — `C`, `"C:"`, `"D:\"` (quote when the value contains
  a colon — `- C:` is parsed as a YAML mapping with key `C`).
- `all` — every growable volume (with safety: never extends system /
  reserved / recovery partitions in this case).

POCO `GrowpartConfig` on `CloudConfig.Growpart`.

### Module

- `Eryph.GuestServices.Provisioning.Modules.GrowpartModule`
  (`Stage.Network`, `Order = 0`, `Frequency = PerBoot`).
- **Per-boot** (not per-instance): host can resize the underlying VHD
  between reboots — per-instance would miss the most common operator
  workflow. Cloud-init's `cc_growpart` is also `per-always`.
- Implementation: WSM via `MSFT_Partition.GetSupportedSize` +
  `MSFT_Partition.Resize` against the storage WMI namespace
  (`root\Microsoft\Windows\Storage`).
- Two Windows-specific safety steps:
  1. `MSFT_Disk.Update()` before partition enumeration — refreshes the
     GPT secondary header that's still sitting at the old end-of-disk
     when the host enlarged the VHD between boots. Cbi does NOT do this;
     without it, `GetSupportedSize` returns the pre-resize value.
  2. WSM (`Root\Microsoft\Windows\Storage`) instead of VDS — sidesteps
     the documented cbi `VDS_E_EXTENT_SIZE_LESS_THAN_MIN` rounding bug
     that cbi swallows in its VDS path.

### OS seam

```csharp
Task<IReadOnlyList<VolumeExtendResult>> ExtendVolumesAsync(
    IReadOnlySet<char>? driveLetterFilter,
    CancellationToken ct);
```

`null` filter ≡ extend every growable volume; a non-null set restricts
to those drive letters.

### End-to-end test

The provisioning e2e suite (`test/e2e/Provisioning.E2E.Tests.ps1`)
provisions a catlet with `drives: - name: sda, size: 64` against a
~40 GB starter image, then asserts the live C: partition is ≥ 60 GB and
grew by ≥ 2 GB versus its pre-boot offline size. The per-boot semaphore
is also checked. See `Context 'growpart extended the OS partition'`.

## What changed from the original Draft — and why

- **Top-level YAML key `extend_volumes:` → `growpart:`.** The Draft
  proposed cbi-style naming (cbi's plugin is `ExtendVolumesPlugin`),
  but cbi has no cloud-config binding for this — its config lives in
  cloudbase-init.conf as `volumes_to_extend`. There is no operator
  precedent for `extend_volumes:` to preserve. Meanwhile cloud-init
  has had `cc_growpart` for years and the project's standing rule —
  [Cloud-init fidelity](../../) — explicitly says "mirror cloud-init's
  structure; question every simplification." This delta represents the
  Draft predating the cloud-init-fidelity decision.
- **POCO** `ExtendVolumesConfig { DriveLetters, SkipRecovery }` →
  `GrowpartConfig { Mode, Devices }`. Same root cause — cloud-init
  shape. The Draft's `SkipRecovery` is folded into module logic: when
  `devices: all` the module refuses to extend protected partition
  types unconditionally (no operator should ever opt INTO eating the
  recovery partition by accident).
- **Stage `Stage.Local` → `Stage.Network / Order 0`.** Two reasons:
  1. Local-stage modules don't yet see user-data in our pipeline —
     `ResolvedUserData` is resolved at the start of the first
     non-Local stage. A Local-stage growpart could not read its own
     `growpart:` cloud-config. The Draft missed this.
  2. The Draft's stated goal — "must run BEFORE WriteFilesModule and
     anything else that might fill C: near its old limit" — is
     satisfied by Network/0 (Network runs before Config where
     WriteFiles lives). Same outcome, different stage.
- **Frequency `PerInstance` → `PerBoot`.** This was an explicit open
  question in the Draft: "someone hot-resizing the VHDX while the
  guest is up and expecting the next boot to pick it up — per-instance
  is wrong for that." Resolved YES per-boot. Cloud-init's `cc_growpart`
  is also `per-always`; cbi's plugin returns `EXECUTE_ON_NEXT_BOOT`.
- **Mechanism** PowerShell `Get-Partition` / `Resize-Partition` →
  direct WMI via `MSFT_Partition.GetSupportedSize` + `.Resize()`. Same
  effect, no `powershell.exe` startup tax (a measurable second per
  invocation), one fewer process layer in the failure path.
- **Workaround beyond cbi**: we call `MSFT_Disk.Update()` before
  enumerating partitions. Without it a guest that booted after the
  host enlarged the VHD between reboots still has its GPT secondary
  header at the old end-of-disk and `GetSupportedSize` returns the
  pre-resize value. Cbi does NOT do this — flagged by sonnet-driven
  review.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — operators who
  need anything beyond "grow to max" still drop a `runcmd`.
- [RFC 0009](0009-module-list-split.md) — `disabledModules: [Growpart]`
  for catlets that manage partitioning via fodder.
