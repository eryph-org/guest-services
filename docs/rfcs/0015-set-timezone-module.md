# RFC 0015 — `set_timezone` cloud-config module

Status: Draft

## Problem

eryph base catlets historically relied on cloudbase-init to honour the
cloud-config `timezone:` key. Now that egs-service replaces cbi on Windows
catlets, that key is silently dropped — there is no `SetTimezoneModule`,
no `Timezone` field on the `CloudConfig` POCO, and no `IWindowsOs` method
to switch the system time zone. We assume `set_timezone` works (see the
team's "currently assume these to work" list); to make that true we need
to ship the module.

## What cloud-init does

A `cc_timezone` module that reads `timezone: "Europe/Berlin"` (an IANA tz
database identifier) and writes the system's `/etc/timezone` (plus the
`/etc/localtime` symlink) on Linux. Schema is a single string — no list,
no enabled flag. Default frequency: per-instance. Fails the module if the
zone name is not recognised by the local tzdata.

Cloud-init reference: <https://cloudinit.readthedocs.io/en/latest/reference/modules.html#timezone>

## What cloudbase-init does

`SetTimezonePlugin` (`cloudbaseinit/plugins/common/setuserpassword.py`'s
sibling) reads the same `timezone:` key, maps the IANA name to a Windows
time zone via an internal CLDR-derived lookup, then calls
`tzutil /s "<Windows tz name>"`. Falls back to the original string if the
mapping already looks Windows-flavoured.

## What Windows needs

The Windows time zone API does NOT accept IANA names. The mapping is:

- IANA: `Europe/Berlin` → Windows: `W. Europe Standard Time`
- IANA: `America/New_York` → Windows: `Eastern Standard Time`
- IANA: `Asia/Tokyo` → Windows: `Tokyo Standard Time`

Microsoft publishes the canonical table at
<https://github.com/unicode-org/cldr/blob/main/common/supplemental/windowsZones.xml>
(or the C# equivalent `TimeZoneInfo.TryConvertIanaIdToWindowsId` available
since .NET 6). We can rely on the BCL: it ships the mapping and
auto-updates with the runtime.

System change mechanism options:

1. **`tzutil /s "<name>"`** — what cbi uses. Subprocess, no .NET dep beyond
   the call. Persists across reboots. Effective immediately.
2. **`Set-TimeZone -Id "<name>"`** — PowerShell. Adds a powershell-startup
   tax we don't want from a service.
3. **`SetDynamicTimeZoneInformation` Win32 API** via P/Invoke. Fastest, no
   external process; bookkeeping marginal.

## Tentative direction

- Add `Timezone: string?` to `CloudConfig` POCO (single string, IANA name).
- Add `Validator` rule: non-empty when present.
- Add `Eryph.GuestServices.Provisioning.Modules.SetTimezoneModule`
  (Stage = Config, Order picked after `RuncmdModule`, Frequency = PerInstance).
- IANA → Windows mapping via `TimeZoneInfo.TryConvertIanaIdToWindowsId` (BCL).
  If conversion fails, log Warning and treat the supplied string as a
  Windows zone name (matches cbi's fallback). If that ALSO fails, module
  returns Failed.
- Apply via `tzutil /s "<windows-name>"` (cbi-compat — minimises behavioural
  drift from the gene corpus that already runs cbi). Wrap behind a new
  `IWindowsOs.SetTimezoneAsync(string windowsTimezoneName, …)` so the
  decorator (`DryRunWindowsOs`) can intercept.
- Validation in `egs-service validate`: ensure the IANA name resolves to a
  known Windows zone at validate-time so bad config is caught before boot.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — per-instance
  frequency contract.
- [RFC 0010](0010-semaphore-design.md) — re-run semantics.

## Open questions

- Should we also support the cbi-only convenience where `timezone:` is
  ALREADY a Windows zone name (no conversion needed)? Cbi does this as a
  fallback. We'd inherit the same forgiving behaviour with a Warning.
- Linux genes targeting the same agent (if/when that exists) would want a
  separate `LinuxOs.SetTimezoneAsync` implementation that writes
  `/etc/timezone` + `/etc/localtime`. Out of scope for v1 — Linux catlets
  still use cloud-init.
- Should `set_timezone` be allowed in `per-boot` mode (operator-overridable
  via [RFC 0009](0009-module-list-split.md) settings)? Default no — time
  zone is a per-instance concept.
