# RFC 0016 — `ntp` cloud-config module

Status: Implemented
Implemented in commit `<pending>`

## Problem

eryph base catlets historically relied on cloudbase-init to honour the
cloud-config `ntp:` block. With egs-service taking over on Windows that
block was silently ignored; time-sync is a hard requirement on most
operator catlets (cert validity, Kerberos, log timestamps).

## What ships

### Schema

```yaml
ntp:
  enabled: true               # default true
  servers: [time.windows.com]
  pools:   [pool.ntp.org]
  real_time_clock_utc: true   # optional; mirrors cbi's real_time_clock_utc
```

POCO `NtpConfig`: `Enabled`, `Servers`, `Pools`, `RealTimeClockUtc`.
Linux-only cloud-init fields (`ntp_client`, `config.*`) are deliberately
absent — cbi ignores them too on Windows.

### Module

- `Eryph.GuestServices.Provisioning.Modules.NtpClientModule`
  (`Stage.Network`, `Order = 3`, `Frequency = PerInstance`).
- Effective server list = `servers ∪ pools`, in input order, whitespace
  trimmed, empties filtered.
- `enabled: false` → stop w32time, set start mode `Disabled`.
- `enabled: true` (default) → set w32time start mode `Automatic`, start
  it, reset SCM triggers (`start/networkon stop/networkoff`), and write
  the manual peer list via `w32tm /config /manualpeerlist:<...>
  /syncfromflags:manual /update`.
- `real_time_clock_utc` is opt-in only — when set, writes
  `HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation\RealTimeIsUniversal`
  to 1 / 0. The default null leaves the Windows default behaviour alone.

### OS seam

- `IWindowsOs.ConfigureNtpClientAsync(bool enabled, IReadOnlyList<string> peers, ...)`
- `IWindowsOs.SetRealTimeClockUtcAsync(bool utc, ...)`
- Service control lives in `Win32\CimService` (Win32_Service CIM —
  `ChangeStartMode`, `StartService`, `StopService`). Triggers still use
  `sc.exe triggerinfo` (no CIM surface for triggers).

### Cloudbase-init parity points

1. Same effective `w32tm` invocation (manualpeerlist + syncfromflags=manual).
2. Same SCM trigger setup (`_set_ntp_trigger_mode` in cbi).
3. Same opt-in `RealTimeIsUniversal` registry write (`set_real_time_clock_utc`).

## What changed from the original Draft — and why

- **Module name** `NtpModule` → `NtpClientModule`. Matches cbi's
  `NTPClientPlugin` type name. Cosmetic.
- **Stage** `Stage.Config` (after `SetTimezoneModule`) → `Stage.Network`
  Order 3. Same rationale as RFC 0015: NTP-before-Runcmd lets runcmd
  entries trust the wall clock. Network/3 also runs *after*
  `ApplyNetworkConfigModule` (Network/2), so by the time `w32tm`
  manualpeerlist is set the network is reachable for the first sync.
- **POCO simplified** — dropped `NtpConfigSection` (Linux-daemon config
  block: `check_exe`, `confpath`, `packages`, `service_name`, `template`).
  The Draft's argument for keeping them was YAML round-trip fidelity;
  on closer look we never *serialise* cloud-config back out — we're a
  consumer, not an emitter — so round-trip fidelity is irrelevant.
  Nested unknown fields inside an `ntp:` block (a custom `ntp_client`
  on a Windows guest, say) pass through the deserialiser via
  `IgnoreUnmatchedProperties` and are silently ignored — they don't
  reach the top-level unknown-key Warning path because they're not at
  top level. Mirrors cloud-init's runtime contract: nested
  additionalProperties are accepted; only top-level surprises log.
- **Added `RealTimeClockUtc`** field. Not in the Draft — caught by a
  sonnet-driven code review of cbi parity. Hyper-V guests with a UTC
  host RTC drift by ±N hours when Windows interprets the RTC as local
  time. Mirrors cbi's `set_real_time_clock_utc`.

## Cross-references

- [RFC 0015](0015-set-timezone-module.md) — companion timezone module.
- [RFC 0009](0009-module-list-split.md) — operators can disable via
  `disabledModules: [NtpClient]` when fodder manages time-sync.
