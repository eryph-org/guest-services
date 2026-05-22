# RFC 0016 — `ntp` cloud-config module

Status: Draft

## Problem

eryph base catlets historically relied on cloudbase-init to honour the
cloud-config `ntp:` block. With egs-service taking over on Windows, that
block is silently ignored: no module, no `Ntp` POCO field, no
`IWindowsOs` method. Time-sync is a hard requirement on most operator
catlets (cert validity, Kerberos, log timestamps) — we cannot ship the
"works as cloudbase-init does" promise without it.

## What cloud-init does

`cc_ntp` is a structured module (richer than `set_timezone`):

```yaml
ntp:
  enabled: true
  ntp_client: auto        # auto | chrony | ntp | ntpdate | systemd-timesyncd
  servers:
    - 0.pool.ntp.org
    - 1.pool.ntp.org
  pools:
    - my-pool.example.org
  config:
    check_exe: chronyd
    confpath: /etc/chrony/chrony.conf
    packages: [chrony]
    service_name: chrony
    template: |
      # custom chrony config template ...
```

On Linux cloud-init:
- Picks the daemon (`ntp_client`) — falls back to whatever the distro
  ships if `auto`.
- Installs the package if missing.
- Writes a config template referencing `servers` + `pools`.
- Restarts the service.
- Default frequency: per-instance.

Reference: <https://cloudinit.readthedocs.io/en/latest/reference/modules.html#ntp>

## What cloudbase-init does

`NTPClientPlugin` reads the same block but maps the entire shape onto a
single Windows daemon — **`w32time`**. Linux concepts that have no Windows
analogue are ignored:

- `ntp_client`: irrelevant on Windows (w32time is the only practical
  option; ntpd / chrony Windows ports exist but eryph genes don't ship
  them).
- `config.check_exe` / `packages` / `service_name` / `template`: ignored.
- `enabled: false`: leave w32time configuration alone (don't disable
  Windows time at all — that breaks Kerberos and certificate validation).
- `pools` and `servers`: merged into the w32time `manualpeerlist`.

The actual Windows operation is roughly:

```
w32tm /config /manualpeerlist:"<servers + pools, space-separated>" `
              /syncfromflags:manual /reliable:no /update
net stop  w32time
net start w32time
w32tm /resync
```

## Tentative direction

### POCO shape

Mirror cloud-init's block as a strongly-typed record. The Linux-only
fields stay on the model so YAML round-trips losslessly, but the Windows
module just ignores them:

```csharp
public sealed record NtpConfig
{
    public bool? Enabled { get; init; }
    public string? NtpClient { get; init; }                  // Linux only
    public IReadOnlyList<string>? Servers { get; init; }
    public IReadOnlyList<string>? Pools { get; init; }
    public NtpConfigSection? Config { get; init; }           // Linux only
}

public sealed record NtpConfigSection
{
    // All Linux-specific. Carried for fidelity; never read on Windows.
    public string? CheckExe { get; init; }
    public string? ConfPath { get; init; }
    public IReadOnlyList<string>? Packages { get; init; }
    public string? ServiceName { get; init; }
    public string? Template { get; init; }
}
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.NtpModule`
  (Stage = Config, Order after `SetTimezoneModule`, Frequency = PerInstance).
- Effective server list = `(servers ?? []) ∪ (pools ?? [])`, de-duped,
  trimmed, comma-rejected.
- If the resulting list is empty, log Info and exit Ok (the operator
  chose to use w32time defaults — `time.windows.com` is fine).
- If `enabled == false`, log Info and exit Ok without touching w32time
  (matches cbi — we don't break Kerberos).

### Windows mechanism

New `IWindowsOs.ConfigureW32TimeAsync(IReadOnlyList<string> peers, …)`
backed by a sequence of `w32tm.exe` / `net.exe` invocations through the
existing `RunArgvCommandAsync` path. Decorator (`DryRunWindowsOs`)
intercepts in dry-run.

A second helper `IsW32TimeAvailableAsync` short-circuits to a Warning if
the service is absent (theoretically possible on a stripped Server Core
image; in practice it's always present on the genes we ship).

### Validation

`egs-service validate` rejects:
- `servers` / `pools` entries that aren't a hostname or IP literal.
- Lists containing the same entry twice (operator confusion; trivial to
  fix).
- `ntp_client` other than the documented set (Warning, not Error — cbi
  ignores it).

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — per-instance
  frequency contract.
- [RFC 0009](0009-module-list-split.md) — operators can disable the
  module via `disabledModules: [NtpModule]` if they manage time-sync via
  their own runcmd / fodder.
- [RFC 0015](0015-set-timezone-module.md) — companion RFC for the timezone
  side of clock configuration.

## Open questions

- Should the empty-server-list case be an explicit `enabled: true` with no
  servers (i.e. assume the operator wants Windows defaults), or an
  error (the operator wrote `ntp:` but supplied nothing meaningful)?
  Tentative: log Info, treat as Ok. Matches cloud-init's permissiveness.
- Should we also enforce `w32time` startup type Automatic? Cbi does not
  touch the start type. Leave it alone in v1 — eryph genes already ship
  `w32time` Automatic.
- `enabled: false` semantics — cloud-init's `cc_ntp` interprets this as
  "don't run the module at all". We do the same (no-op + log). Document
  it explicitly so an operator who wants to *stop* w32time uses a
  `runcmd` instead.
- Linux Windows-only model: when egs-service grows a Linux provisioning
  path, `NtpModule` will need a Linux branch that does the daemon
  selection / package install / config-template work cloud-init does
  today. Out of scope for v1.
