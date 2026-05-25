# Stages

The agent runs four stages in order — `Local`, `Network`, `Config`, `Final` —
with the same names and intent as cloud-init.

| Stage | Modules | Purpose |
| --- | --- | --- |
| Local | (none) | Platform setup before networking. Reserved for future use. |
| Network | Growpart, SetHostname, ApplyNetworkConfig, NtpClient, Timezone, SetLocale | Identity and IP, settled before anything talks over the network. |
| Config | UsersGroups, SetPasswords, SshModule, WriteFiles, Runcmd, Licensing | Host configuration — most of the cloud-config schema. |
| Final | WriteFilesDeferred, ScriptsUser, PowerState | Deferred writes, user scripts, and an optional controlled reboot, after everything else has run. |

Within a stage, modules run in a fixed order. The order is part of each module
and can't be changed; which modules run in a stage can — see the `stages` block
in [settings](settings.md).

## How a run proceeds

The agent works through the stages top to bottom. In each stage it runs the
eligible modules in order, skipping any whose semaphore is already set. User-data
is parsed once, the first time a stage past `Local` needs it. After the Final
stage finishes, the agent runs the datasource's cleanup step — on Azure that
deletes `CustomData.bin`; for the other datasources it does nothing.

## Running one stage

```powershell
egs-service run --stage network
egs-service run --stage config
```

User-data is parsed only when needed, so `--stage local` skips the parse; the
other stages parse it on entry because their modules depend on it.

## Reboot during a run

When a module needs a reboot — `SetHostname` after a rename that Windows defers,
or a `runcmd`/script that exits `1003` — the run stops, the agent records that
module as done so it isn't repeated, and the guest reboots (`shutdown /r`, unless
`--dry-run`). On the next boot the per-instance markers survive, so the agent
skips the completed modules and continues from where it left off.

## cloud-init mapping

| cloud-init stage | This agent |
| --- | --- |
| `init-local` | Local |
| `init` (network) | Network |
| `modules:config` | Config |
| `modules:final` | Final |

cloud-init's `cloud_init_modules` / `cloud_config_modules` /
`cloud_final_modules` lists map to the per-stage `enabledModules` /
`disabledModules` settings; stage membership and order stay fixed.
