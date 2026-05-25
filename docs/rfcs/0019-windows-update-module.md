# RFC 0019 — `windows_update` cloud-config module

Status: Draft

## Problem

Operators routinely want to toggle Windows Update behaviour at first
boot. Common asks we see in gene support:

- "Disable Windows Update entirely on this sysprep'd lab image."
- "Set the Automatic Updates schedule to 3 AM daily."
- "Disable driver search so my OEM-supplied drivers stick."

There's no cloud-config key for any of this today. Each gene that wants
it ships a registry-editing PowerShell script. Bringing this into the
agent gives the operator a single declarative knob and lets the genes
drop the bespoke scripts.

## What cloud-init does

Nothing — there is no Windows-update equivalent in the cloud-init
modules index
(<https://cloudinit.readthedocs.io/en/latest/reference/modules.html>).
The closest concept is `cc_package_update_upgrade_install`, which is
Linux package-manager-shaped and doesn't translate.

## What cloudbase-init does

`windowsautoupdate.py` — toggles a single registry value
(`HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU\NoAutoUpdate`)
based on a service-config key. See
<https://github.com/cloudbase/cloudbase-init/blob/master/cloudbaseinit/plugins/windows/winrmlistener.py>
neighbours (the file is in the same `plugins/windows/` directory).

Cbi's coverage is narrow: it's the on/off switch only. We want
richer scheduling and driver-search controls.

## What Windows needs

The AU policy registry tree under
`HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` (DWORD
values, all):

- `NoAutoUpdate` — 0 = auto-install enabled, 1 = disabled.
- `AUOptions` — 2 (notify), 3 (auto-download, notify install),
  4 (auto-install on schedule), 5 (let users choose). We map
  `auto_install: true|false` → 4 or 2.
- `ScheduledInstallDay` — 0 = every day, 1 = Sunday … 7 = Saturday.
- `ScheduledInstallTime` — hour 0-23.

Driver search lives in a different tree:
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching`,
DWORD `SearchOrderConfig`. 0 = don't search Windows Update, 1 = search
if not found locally, 2 = always search (default). We map
`install_drivers: false` → 0.

The service itself (`wuauserv`) can be disabled via
`Set-Service wuauserv -StartupType Disabled` (and `sc.exe config wuauserv start= disabled`
as the non-PowerShell fallback that the rest of the codebase prefers).

Note: Server SKUs without the Windows Update service (Server Core
"FOD-stripped") will fail the `Set-Service` call gracefully — log
Warning and continue on the registry edits.

## Tentative direction

### POCO shape

```csharp
public sealed record WindowsUpdateConfig
{
    public bool? Enabled { get; init; }            // service Start = Automatic / Disabled
    public bool? AutoInstall { get; init; }        // AUOptions 4 vs 2
    public int? ScheduleDay { get; init; }         // 0 = daily, 1-7 = Sun-Sat
    public int? ScheduleHour { get; init; }        // 0-23
    public bool? InstallDrivers { get; init; }     // SearchOrderConfig 1 vs 0
}
```

Top-level YAML:

```yaml
windows_update:
  enabled: true
  auto_install: true
  schedule_day: 0
  schedule_hour: 3
  install_drivers: true
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.WindowsUpdateModule`
  (Stage = Config, Order after `NtpModule` so time-sync is sane before
  any Windows Update connection happens. Frequency = PerInstance.)
- `enabled: false` → set `wuauserv` startup type Disabled, stop the
  service. Do NOT also write `NoAutoUpdate=1` (let the service-disable
  speak for itself; an operator who re-enables the service should see
  the AU config they expected).
- `enabled: true` → set startup type Automatic. Then apply the AU policy
  values that were supplied; omitted fields are left untouched.
- Each field that's null in the POCO → no change (matches cloud-init's
  three-state pattern: explicit value, explicit-null = leave alone).

### IWindowsOs additions

```csharp
Task SetServiceStartupTypeAsync(string serviceName, ServiceStartupType type, CancellationToken ct);
Task<bool> ServiceExistsAsync(string serviceName, CancellationToken ct);
Task WriteRegistryDwordAsync(string keyPath, string valueName, int value, CancellationToken ct);
Task DeleteRegistryValueAsync(string keyPath, string valueName, CancellationToken ct);
```

(Service helpers are also useful for `LicensingModule` rearm-and-reboot
paths and any future module that toggles a service — keep them general.)

### Validation

- `schedule_day` outside `0..7` → validation error.
- `schedule_hour` outside `0..23` → validation error.
- `windows_update:` block with all fields null → log Info, exit Ok.
  Same forgiveness as `cc_ntp`.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — PerInstance
  frequency contract.
- [RFC 0009](0009-module-list-split.md) — operators who manage Windows
  Update via Group Policy / WSUS disable via `disabledModules:
  [WindowsUpdateModule]`.
- [RFC 0016](0016-ntp-module.md) — sibling Windows-system-service
  module; same POCO null-means-unchanged convention.

## Open questions

- WSUS pointer (`HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\WUServer`)
  is a related concept; should it live in this module or in a separate
  `wsus:` block? Tentative: same module, additional fields when
  someone actually needs it. Defer to next-RFC.
- `auto_install: false` maps to AUOptions=2 (notify). Some operators
  expect "don't even notify" — that's a different combination
  (NoAutoUpdate=1 + service Automatic). Document explicitly.
- Server vs Client SKU: the AU policy semantics are subtly different on
  Server SKUs (the in-box settings UI is gone, but the registry tree is
  still the source of truth). Confirm with a Server 2022 catlet.
- Do we ever need to KICK an update check (`UsoClient StartScan`) after
  flipping the config? Tentative no — cbi doesn't, and operators who
  want it can `runcmd`.
