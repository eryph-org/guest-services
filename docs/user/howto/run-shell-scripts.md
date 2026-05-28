# Run shell scripts

There are three ways to run a script from user-data (a fourth, `#cloud-boothook`,
is stored but not run):

1. A `runcmd:` entry in `#cloud-config` — runs in the Config stage.
2. A multipart part with `Content-Type: text/x-shellscript` — runs in the Final
   stage.
3. The whole user-data with a `#ps1`, `#ps1_sysnative`, or `#!` first line — also
   Final stage.

## How the runner is chosen

A script runs by its filename extension first, then its shebang, then its
content type:

| Filename ends in | Runs as |
| --- | --- |
| `.ps1` | `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <path>` |
| `.cmd`, `.bat` | `cmd.exe /c <path>` |
| `.sh` | skipped — no POSIX shell on Windows |

With no usable extension, a `#ps1`/`#ps1_sysnative` shebang runs under
PowerShell and a `#!/...` shebang is skipped. As a last resort, a part typed
`text/x-shellscript` runs under PowerShell. A script the agent can't classify is
skipped with a warning, never run blindly.

## Where scripts go

Scripts are staged under
`%ProgramData%\eryph\provisioning\scripts\per-instance\` as
`<order>-<filename>` (a part named `enable_rd.ps1` becomes `001-enable_rd.ps1`).
Each script's output goes to
`%ProgramData%\eryph\provisioning\logs\<order>-<filename>.log` with the exit code
and full stdout and stderr.

## Reboots and failures

Two exit codes ask the guest to reboot mid-run:

- `1001` — reboot, then move on to the next script / runcmd entry.
- `1003` — reboot, then re-run **this same script / entry**. Use for
  multi-stage installers; branch on `EGS_REBOOT_COUNT` (see
  [Runcmd](../reference/modules.md#runcmd) for the full env var list).

Any other non-zero exit is logged and the next script still runs. The exit
code is in the per-script log.

A script (whether a runcmd entry or a multipart-shebang script) may ask for at
most `reboot.maxPerScript` reboots (default 10) before it is failed. A script
can raise its own cap with `##egs.reboot_limit=<n>` on stdout.

## Example: a script in a multipart payload

```
Content-Type: multipart/mixed; boundary="=B="
MIME-Version: 1.0

--=B=
Content-Type: text/x-shellscript
Content-Disposition: attachment; filename="install.ps1"

Install-WindowsFeature Web-Server -IncludeManagementTools

--=B=--
```

After provisioning:

```powershell
Get-Content C:\ProgramData\eryph\provisioning\logs\001-install.ps1.log
```
