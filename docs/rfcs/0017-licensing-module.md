# RFC 0017 — `licensing` cloud-config module

Status: Draft

## Problem

Windows licensing on cloud catlets is handled today by a per-gene
PowerShell script (`rearm-evaluation.ps1`) that slmgr-rearms expiring
evaluation images. There is no cloud-config key for licensing, so any
operator who wants to ship a product key, point at a KMS host, or just
toggle activation has to drop into `runcmd` or a fodder script. Genes
duplicate this logic; the operator-visible surface is "go write some
PowerShell."

Bringing licensing into the provisioning agent gives the gene corpus a
canonical place to express the same intent and lets the rearm script
disappear into the module.

## What cloud-init does

No `cc_licensing` module — Linux licensing isn't a concept upstream
cares about. There's no analogue in the
<https://cloudinit.readthedocs.io/en/latest/reference/modules.html>
index.

## What cloudbase-init does

No first-class plugin either. Cloudbase-init relies on operator-supplied
scripts (the same path eryph genes use). See
<https://github.com/cloudbase/cloudbase-init/blob/master/cloudbaseinit/plugins/>
— no `licensing.py` / `slmgr.py`.

This is one of the rare places we deliberately go beyond cbi: it's the
only way to fold the gene-side `rearm-evaluation.ps1` back into the
agent without forking the fodder corpus.

## What Windows needs

`slmgr.vbs` is the licensing tool on every Windows SKU. It runs as
`cscript C:\Windows\System32\slmgr.vbs <verb> [args]` and is the
mechanism cbi-shaped genes already use. The verbs we care about:

- `/ipk <key>`  — install product key
- `/skms <host>[:port]`  — set KMS host
- `/skms-port <port>`  — set KMS port (older Windows; combined in `/skms` on newer)
- `/ckms`  — clear configured KMS host (when operator removes the key)
- `/ato`  — activate now
- `/rearm`  — reset evaluation timer; exits with success and requires reboot
- `/xpr`  — expiration query (parseable text output)
- `/dli`  — license details (parseable text output)

Exit code 1003 from a `/rearm` matches the cbi-and-gene convention for
"reboot and continue," so the module can request reboot via the standard
`ModuleOutcome.RebootRequested` path.

## Tentative direction

### POCO shape

```csharp
public sealed record LicensingConfig
{
    public bool? Enabled { get; init; }
    public string? ProductKey { get; init; }
    public string? KmsHost { get; init; }
    public int? KmsPort { get; init; }            // default 1688 if KmsHost set
    public bool? RearmOnExpiry { get; init; }     // attempt /rearm if eval expiring
    public bool? Activate { get; init; }          // call /ato after key / KMS install
}
```

Top-level YAML:

```yaml
licensing:
  enabled: true
  product_key: AAAAA-BBBBB-CCCCC-DDDDD-EEEEE
  kms_host: kms.example.com
  kms_port: 1688
  rearm_on_expiry: true
  activate: true
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.LicensingModule`
  (Stage = Final, runs after the rest of provisioning so the box is in
  its intended shape before activation. Frequency = PerInstance.)
- Order in Final: after `RuncmdModule` so operator scripts have first
  crack at licensing if they want it.
- `enabled: false` → log Info and exit Ok without touching slmgr.
- `rearm_on_expiry: true` → call the eval-state probe first; only invoke
  `/rearm` if the probe says we're expiring or expired. A successful
  `/rearm` returns `ModuleOutcome.RebootRequested` (carrying the
  reboot-and-continue contract).
- `product_key` + `activate: true` → `/ipk` then `/ato`. Skip `/ato` if
  the key install reports a no-op (already that key).
- `kms_host` set → `/skms host[:port]` then (when `activate: true`)
  `/ato`. `kms_host: ""` explicitly clears via `/ckms`.

### IWindowsOs additions

```csharp
Task InstallProductKeyAsync(string key, CancellationToken ct);
Task SetKmsHostAsync(string host, int? port, CancellationToken ct);
Task ClearKmsHostAsync(CancellationToken ct);
Task<LicensingActivationResult> ActivateLicenseAsync(CancellationToken ct);
Task<LicensingEvalState> GetEvaluationStateAsync(CancellationToken ct);
Task<RearmResult> RearmEvaluationAsync(CancellationToken ct);
```

All five go through `RunArgvCommandAsync` against `cscript.exe` + the
absolute slmgr.vbs path so the `DryRunWindowsOs` decorator can intercept.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — exit-1003
  reboot-and-continue contract that `RearmEvaluation` rides on.
- [RFC 0009](0009-module-list-split.md) — operators on owned images that
  manage licensing externally disable via `disabledModules: [LicensingModule]`.
- [RFC 0010](0010-semaphore-design.md) — PerInstance frequency means the
  module only runs once per instance-id, which is the right granularity
  for activation.

## Open questions

- How to detect "evaluation expiring" cheaply. Options: parse `/xpr`
  output (locale-dependent — risky), parse `/dli` (also localised),
  query WMI `SoftwareLicensingProduct.GracePeriodRemaining` (cleanest,
  but the property is per-SKU and a stripped image might not have it).
  Tentative: WMI first, parse `/xpr` as a fallback.
- Should `product_key: ""` (empty string) be a `/cpky` "clear product
  key" request or a validation error? Tentative: validation error —
  operators who want to clear a key can use `runcmd`. Empty-string-means-
  delete is an easy footgun.
- Do we re-`/ato` on every PerInstance run when neither key nor KMS host
  changed? Tentative: yes, idempotent and cheap.
- Once this module ships, `rearm-evaluation.ps1` becomes redundant in
  the gene corpus. Document the migration path in the gene README.
