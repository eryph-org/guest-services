# How to run shell scripts

Cloud-init defines four ways to ship a script in user-data:

1. As a `runcmd:` entry inside `#cloud-config`.
2. As a MIME part with `Content-Type: text/x-shellscript`.
3. As the entire user-data with a `#ps1`, `#ps1_sysnative` or `#!` shebang.
4. As `#cloud-boothook` (captured but **not executed** in v1 — see [RFC 0013](../../rfcs/0013-boothook-execution.md)).

Routes 2 and 3 produce script *payloads* that the `ScriptsUser` module
runs in the Final stage. Route 1 runs in the Config stage via the
`Runcmd` module. See [Modules reference](../reference/modules.md).

## Filename-led dispatch

The agent decides how to run a script payload by **filename extension
first** (matching cloudbase-init), then shebang, then content-type:

| Filename ends in | Runner | Notes |
| --- | --- | --- |
| `.ps1` | `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <path>` | |
| `.cmd`, `.bat` | `cmd.exe /c <path>` | |
| `.sh` | (skipped) | No POSIX shell on Windows; a warning is logged. |

If no usable filename extension is present, the agent falls back to
shebang detection (`#ps1`, `#ps1_sysnative` → PowerShell). A POSIX
shebang (`#!/...`) is skipped with a warning on Windows. As a last
resort, `Content-Type: text/x-shellscript` is treated as PowerShell
with a warning logged.

See [RFC 0007](../../rfcs/0007-scripts-per-frequency-edge-cases.md) for the rationale.

## Where scripts are staged

`%ProgramData%\eryph\provisioning\scripts\per-instance\<ordinal>-<filename>`
— ordinal is the declaration order, filename is preserved verbatim
(invalid path chars sanitized to `_`). So a part shipped as
`enable_rd.ps1` ends up on disk as e.g. `001-enable_rd.ps1`.

Per-script logs land in
`%ProgramData%\eryph\provisioning\logs\<ordinal>-<filename>.log`
with the script path, exit code, and full stdout/stderr.

## Reboot-and-continue (exit 1003)

A script that exits with **1003** signals "reboot now, then resume
provisioning". The agent:

1. Marks the module's semaphore so the rest of the modules in this stage
   re-evaluate after the reboot.
2. Calls `shutdown.exe /r /t 5`.
3. On boot the agent re-runs; the reboot-requesting module sees the
   semaphore and resumes from there.

This is a cloudbase-init convention. Cloud-init does not honor it; on
cloud-init you'd use `power_state` instead.

## Non-zero exit (≠ 1003)

Logged as an error; provisioning continues with the next script. The
exit code lands in the per-script log and in `state.json` for that run.

## Path to a Final-stage script via MIME multipart

```
Content-Type: multipart/mixed; boundary="=B="
MIME-Version: 1.0

--=B=
Content-Type: text/x-shellscript
Content-Disposition: attachment; filename="install.ps1"

Write-Host 'installing'
Install-WindowsFeature Web-Server -IncludeManagementTools

--=B=--
```

After provisioning:
```powershell
Get-Content C:\ProgramData\eryph\provisioning\logs\001-install.ps1.log
```
