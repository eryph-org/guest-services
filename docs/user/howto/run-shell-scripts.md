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

Two cbi-style exit codes ask the guest to reboot mid-run:

- `1001` — reboot, the script is done. The next script (or runcmd entry) runs
  on the boot after.
- `1003` — reboot and **re-run this same script / entry**. Use this for
  multi-stage installers (driver → reboot → role → reboot → done). It is up
  to the script to know which stage it's on; for runcmd entries the agent
  exposes `EGS_RUNCMD_REBOOT_COUNT` so the script can branch.

Any other non-zero exit is logged and the next script still runs. The exit code
is in the per-script log.

A runcmd entry that asks for repeated reboots is bounded by a per-entry quota
(default 10). The script can raise its own cap by emitting
`##egs.runcmd.reboot_limit=<n>` on stdout. See
[Runcmd](../reference/modules.md#runcmd) for the full contract, and
[settings](../reference/settings.md#runcmd) for the global tunables.

`ScriptsUser` (multipart-shebang scripts) honours `1001` and `1003` similarly,
but a single script is bounded by `reboot.maxPerScript` (default 2) — tighter
than runcmd because user scripts typically don't need many reboots.

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
