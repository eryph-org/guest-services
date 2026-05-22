# RFC 0007 — `scripts/per-*` semantics edge cases

Status: Implemented

## Problem

Cloud-init's `scripts/per-instance`, `scripts/per-boot`, `scripts/per-once` directories execute scripts at appropriate frequencies. Several edge cases need explicit policy:

- Execution order within a directory (alphabetical? declaration order from user-data?)
- Script that exits non-zero: continue or abort the stage?
- Script that requests reboot via exit-1003: handled like a module reboot request?
- Permissions: do we chmod +x equivalent on Windows (set NTFS execute ACL)?
- Stdout/stderr capture: log? send to reporting events?

## What cloud-init does

- Alphabetical execution order.
- Non-zero exit: log Error, continue to next script (unless `power_state` or `runcmd` was the failure — those propagate).
- No reboot-and-continue: cloud-init isn't designed for that pattern. Script that wants reboot uses `power_state_change` module instead.
- Permissions: Linux chmod 755 on the file before execution.
- Stdout/stderr: redirected to `/var/log/cloud-init-output.log`.

## What cloudbase-init does

Runs scripts in declaration order. Exit code 1003 = reboot-and-continue (the convention we already adopted for `RuncmdModule`). Stdout/stderr logged via the cloudbase-init log.

## cbi quirks we must accommodate

Real eryph fodder (genes in `S:\eryph\eryph-genes\src`) has been crafted around two
cloudbase-init bugs that affect script handling:

1. **cbi requires `filename=` in the multipart `Content-Disposition` header.** Parts
   without a filename are dropped. Eryph genes therefore always include one.
2. **cbi ignores shebangs.** A part containing `#ps1_sysnative` / `#!` is not
   dispatched by shebang; cbi dispatches by the filename's extension only.
   `.ps1`, `.cmd`, `.bat`, `.sh` decide the runner.

The user-data we observed in the e2e run reflects this — the parent gene's
`enable_rd.ps1` has `Content-Type: text/x-shellscript` and `filename="enable_rd.ps1"`
but **no shebang**. cbi would run it as PowerShell because the extension is `.ps1`;
our first cut classified it as `Kind = Other` and silently dropped it.

So our script-kind detection must mirror cbi's actual behavior (filename-led),
not cloud-init's documented behavior (shebang-led).

## Decisions

- **Script-kind detection priority (filename-led):** `filename=` extension
  first (`*.ps1` → PowerShell, `*.cmd`/`*.bat` → cmd, `*.sh` → Other-with-warning
  on Windows), then shebang (`#ps1_sysnative`, `#ps1`; `#!/...` resolves to
  Other-with-warning on Windows), then `Content-Type: text/x-shellscript`
  falls back to PowerShell on Windows with a warning, otherwise Other with
  a warning logged. Implemented by `ScriptKindDetector`. The two real
  producers (eryph genes via cbi, and cloud-init-aware tooling) both win
  under this ordering.
- **On-disk filename:** preserve the `filename=` value from the MIME part
  (extension included), prefixed with the declaration order
  (`001-enable_rd.ps1`, `002-rearm-evaluation.ps1`). Genes embed meaningful
  filenames; we keep them so guest-side logs match what an operator wrote.
  Missing filenames are auto-generated as `00N-script.{ext}` from the
  inferred kind.
- **Order:** declaration order from user-data, then alphabetical for anything
  written to the directory directly. Eryph genes emit scripts in a specific
  order and we shouldn't reshuffle.
- **Non-zero exit:** log Error, continue. Matches both cbi and cloud-init.
- **Exit 1003 → `RebootRequested` outcome.** Mid-stage abort; resume on next
  boot via the standard reboot-and-continue mechanism. (Cloud-init doesn't
  do this; we do because cbi-compat matters for eryph genes —
  `rearm-evaluation.ps1` uses 1003.)
- **Permissions:** skip (Windows doesn't care about execute bits).
- **Stdout/stderr:** capture, emit as `ReportingEvent.Progress` (one per
  script), and write to a per-script log file under
  `%ProgramData%\eryph\provisioning\logs\<script-name>.log`.
- **cbi-bug shape (no filename, no shebang, content-type only):** accepted
  best-effort — falls back to PowerShell on Windows via content-type with a
  warning logged. cbi rejects this shape (no filename) but a hand-written
  cloud-config might produce it, and the warning preserves the audit trail.

## Open questions

- Cloud-init's "boothook" scripts run BEFORE `cloud-init-network.service`
  (very early). Our model runs everything after Network stage — does that
  break any eryph use case? (Probably no — `#cloud-boothook` is exotic.)
- Should script execution timeout be configurable per script, or one global
  timeout from `egs-provisioning.json`?
- What happens if `runcmd` (staged in Config) writes a script that the
  user-data ALSO supplied as `#!/...`? Two files in `scripts/per-instance/`.
  Define a name-collision policy.
