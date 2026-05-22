# RFC 0023 — Windows `extend_volumes` cloud-config module

Status: Draft

## Problem

A catlet whose OS disk was sized larger than the source image's
partition boots with the extra capacity sitting as unallocated space at
the end of the disk — Windows does not auto-extend partitions to fill
the disk. Operators hit this constantly: "I asked for a 64 GB disk and
`C:` is still 20 GB." Today the workaround is a `runcmd` line invoking
`Resize-Partition` or a diskpart script, both of which are fragile
(partition number guesses, recovery-partition collisions, missing the
fact that the volume might already be at max size).

cloudbase-init has shipped a dedicated `extend_volumes` plugin for
exactly this; eryph base catlets currently lean on fodder `runcmd`. We
should ship a real module so the cloud-config key `extend_volumes:`
works as it did under cbi.

## What cloud-init does

`cc_growpart` resizes a partition with `growpart`/`gdisk`, and the
companion `cc_resizefs` then grows the filesystem to fill the new
partition. Both are Linux-only — they rely on ext4 / xfs / btrfs
online-resize support. Schema:

```yaml
growpart:
  mode: auto                # auto | growpart | gpart | off
  devices: ['/']
  ignore_growroot_disabled: false
resize_rootfs: true
```

Cloud-init reference:
<https://cloudinit.readthedocs.io/en/latest/reference/modules.html#growpart>

## What cloudbase-init does

`cloudbaseinit/plugins/windows/extendvolumes.py` — iterates the
configured volume labels (drive letters by default), reads
`Win32_DiskPartition` for each, and calls `diskpart` with a
`extend filesystem` script to consume any unallocated space adjacent to
the partition. Default: extend `C:` only; recovery / system partitions
are skipped.

Source:
<https://github.com/cloudbase/cloudbase-init/blob/master/cloudbaseinit/plugins/windows/extendvolumes.py>

## What Windows needs

Modern PowerShell on supported Server/Client SKUs exposes a cleaner
path than `diskpart`:

1. `Get-Partition -DriveLetter <L>` — find the partition.
2. `Get-PartitionSupportedSize -DriveLetter <L>` — returns
   `(SizeMin, SizeMax)`; `SizeMax` already accounts for unallocated
   space adjacent to the partition. No `SizeMax > CurrentSize` → no-op.
3. `Resize-Partition -DriveLetter <L> -Size <SizeMax>` — grows the
   partition AND the NTFS filesystem in one call.
4. Skip when `Get-Partition.Type` is `Recovery` or `System`, or when
   the disk is offline/read-only.

`diskpart` remains a fallback for older builds, but the genes we ship
target Server 2019+ / Win10+, where `Resize-Partition` is universal.

## Schema

Smaller than the cloud-init schema because Windows doesn't expose the
same knobs:

```yaml
extend_volumes:
  drive_letters: [C]            # explicit list; omit/[]= "all OS fixed volumes"
  skip_recovery: true           # default; never resize recovery / system
```

`drive_letters` is the only knob we expect operators to set. When
absent or empty the module enumerates all fixed-disk OS volumes
(`Get-Volume -DriveType Fixed`) and grows each one whose
`SizeMax > CurrentSize`.

## Tentative direction

### POCO sketch

```csharp
public sealed record ExtendVolumesConfig
{
    public IReadOnlyList<string>? DriveLetters { get; init; }
    public bool? SkipRecovery { get; init; }     // default true
}
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.ExtendVolumesModule`
  (`[Stage(Stage.Local, Order = ..., Frequency = ModuleFrequency.PerInstance)]`).
- Local stage on purpose — must run BEFORE `WriteFilesModule` and
  anything else that might fill `C:` near its old limit.
- New `IWindowsOs.ExtendVolumeAsync(char driveLetter, bool skipRecovery, …)`
  returning a small result record `(WasExtended, OldSize, NewSize)`
  so the module can log a clear summary.
- Each drive letter is processed independently: a single bad letter
  logs Warning and the module moves on. Module never returns Failed
  for "nothing to do".

### Validation

`egs-service validate` rejects:
- `drive_letters` entries that aren't `A`-`Z` (single ASCII letter).
- Duplicates within the list (Warning, not Error).

## Open questions

- Should the module attempt to bring offline disks online first? cbi
  does not; an offline disk is usually an operator signal. Tentative:
  skip with Info log, document the workaround (a `runcmd` line that
  sets the disk online before the module runs).
- Per-boot vs per-instance: someone hot-resizing the VHDX while the
  guest is up and expecting the next boot to pick it up. Per-instance
  is wrong for that. Tentative: keep per-instance for v1 (matches cbi),
  add a per-boot opt-in later if a real consumer surfaces.
- Storage Spaces / dynamic volumes: out of scope. Document explicitly.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — operators who
  need anything fancier than "grow to max" still drop a `runcmd`.
- [RFC 0009](0009-module-list-split.md) — disable via
  `disabledModules: [ExtendVolumesModule]` when fodder owns the resize
  itself.
