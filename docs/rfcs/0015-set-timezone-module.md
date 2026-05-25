# RFC 0015 — `timezone` cloud-config module

Status: Implemented
Implemented in commit `<pending>`

## Problem

eryph base catlets historically relied on cloudbase-init to honour the
cloud-config `timezone:` key. With egs-service replacing cbi on Windows
catlets, that key was silently dropped.

## What ships

- POCO: `string? Timezone` on `CloudConfig` (single IANA name, e.g.
  `Europe/Berlin`). Quote the value in YAML when it could be parsed as a
  YAML alias — IANA names rarely collide but `Z`, `UTC` etc. are safe.
- Module: `Eryph.GuestServices.Provisioning.Modules.TimezoneModule`
  (`Stage.Network`, `Order = 4`, `Frequency = PerInstance`).
- Mechanism: IANA → Windows id via `TimeZoneInfo.TryConvertIanaIdToWindowsId`
  (CLDR mapping shipped with the BCL since .NET 8). Applied via
  `tzutil /s "<windows-id>"`. If the input is already a Windows id we
  accept it verbatim (cbi-compat fallback).
- OS seam: `IWindowsOs.SetTimezoneAsync(string windowsTimezoneId, ...)`.
  `DryRunWindowsOs` intercepts; the unit tests substitute the interface.
- Failure surface: unknown id (not IANA, not Windows) returns
  `ModuleOutcome.Failed` before any system call. A `tzutil` non-zero
  exit propagates as `Failed` with the raw exit info in the message.

## What changed from the original Draft — and why

- **Module renamed** `SetTimezoneModule` → `TimezoneModule`. Cosmetic;
  cloud-init's module is `cc_timezone`, cbi's is `SetTimezonePlugin`.
  Either is defensible. The shorter name matches the cloud-config key.
- **Stage moved** `Stage.Config` (after RuncmdModule) → `Stage.Network`
  Order 4. The Draft's "after RuncmdModule" placement was a planning
  oversight: `RuncmdModule` lives at `Stage.Config / Order 4`, and
  putting timezone *after* it means operator runcmd entries — which
  routinely create scheduled tasks, write log lines, and start
  background work — run with a still-wrong system timezone. Moving
  timezone earlier (Network/4) makes runcmd execute under the
  configured zone. Same rationale applies to NTP and SetLocale.
- **Open question "accept Windows zone names verbatim"** → resolved YES.
  Cbi-compat fallback preserved.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — per-instance
  frequency contract.
- [RFC 0016](0016-ntp-module.md) — companion NTP module.
