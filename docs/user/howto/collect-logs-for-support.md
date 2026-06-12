# How to collect logs for support

`egs-service collect-logs <OUTPUT.zip>` bundles everything a support
engineer needs into one archive.

## Run it

```powershell
egs-service collect-logs C:\Temp\egs-bundle.zip
```

The command never prompts and overwrites the output file if it exists.

## What's in the bundle

| Entry | Source | When useful |
| --- | --- | --- |
| `state.json` | `%ProgramData%\eryph\provisioning\state.json` | The agent's view of what ran. Look here first. |
| `logs/agent.log` | `%ProgramData%\eryph\guest-services\logs\agent.log` (Linux: `/var/log/eryph/guest-services/agent.log`) | The whole agent's operational log — datasource discovery, every module, reboots, and remote-access (SSH) events. Look here when something misbehaves. |
| `logs/<script>.log` | `%ProgramData%\eryph\provisioning\logs\` | Per-script stdout/stderr + exit code from `ScriptsUser` and any other module that writes there. |
| `scripts/<ordinal>-<filename>` | The staged user-data scripts directory | The scripts as the agent actually ran them — handy for diffing against the fodder. |
| `version.txt` | generated | The agent version and the time the bundle was made. |

Files the agent couldn't open (locked log files etc.) are skipped
silently; the bundle is best-effort.

## Inspect without unpacking

```powershell
Expand-Archive C:\Temp\egs-bundle.zip C:\Temp\egs-bundle\
Get-Content C:\Temp\egs-bundle\state.json
Get-Content C:\Temp\egs-bundle\logs\agent.log
Get-Content C:\Temp\egs-bundle\logs\001-enable_rd.ps1.log
```

## What's *not* in the bundle

- The Windows Event Log. The agent's operational log is captured in
  `logs/agent.log` (above), so you rarely need the Event Log — but the
  service also mirrors the same events there, so export it separately if
  you need the OS-side timestamps or events from before `agent.log`
  rolled over.
- The OS / Hyper-V system logs.
- The original cloud-init datasource payload. If you can read it from
  inside the guest (e.g. mount the cidata ISO), include it manually —
  the agent never copies it because it can be large and may contain
  secrets.

## Sending it on

The bundle is plain zip; standard PII rules apply. `state.json` contains
the instance id and the list of modules that ran but no user-supplied
secrets. Per-script logs may contain whatever your scripts printed —
review them before sharing.
