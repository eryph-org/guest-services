# Your first catlet with cloud-config

The eryph workflow: ship a minimal cloud-config fodder set with a
catlet, boot it, and see the provisioning agent apply it.

## Prerequisites

- An eryph host (eryph-zero) with a Windows parent gene that pre-installs
  `egs-service` (e.g. `dbosoft/winsrv2022-standard:starter`).
- `eryph` CLI on the host.

## Step 1 — author a catlet config

Save the following as `demo-catlet.yaml`:

```yaml
name: demo
parent: dbosoft/winsrv2022-standard/starter

cpu:
  count: 2
memory:
  startup: 2048

fodder:
  - name: cloud-config
    type: cloud-config
    content:
      hostname: demo-guest
      users:
        - name: alice
          plain_text_passwd: ChangeMe!42
          groups: [Administrators]
      write_files:
        - path: C:\demo\hello.txt
          content: "hello from cloud-config"
      runcmd:
        - powershell.exe -NoProfile -Command "Write-Host 'runcmd ran'"
```

## Step 2 — create the catlet

```powershell
gc demo-catlet.yaml | New-Catlet
```

eryph-zero attaches a NoCloud `cidata` seed disk to the VM with the fodder
serialised as cloud-init user-data. On first boot the agent picks it up.

## Step 3 — watch the provisioning state

The agent reports through Hyper-V KVP. From the host:

```powershell
egs-tool get-status <vm-id>
```

When it returns `completed`, the agent has finished the Final stage.
Alternatively, SSH into the guest and inspect `state.json`. Set up the SSH
config once for the VM — pass the same `<vm-id>` you used with `get-status`
above — giving it the alias `demo`, then connect by that alias:

```powershell
egs-tool add-ssh-config <vm-id> demo
ssh demo
Get-Content C:\ProgramData\eryph\provisioning\state.json
```

You should see the instance id, the four completed stages, and the
modules that ran for this instance.

## Step 4 — verify the run

```powershell
ssh demo
hostname                          # demo-guest
Get-Content C:\demo\hello.txt     # hello from cloud-config
Get-LocalUser alice               # exists, in Administrators
```

## Step 5 — re-run on the same VM

On the same instance the agent skips modules that already ran. Changing
the fodder and reloading the catlet doesn't re-run the per-instance
modules — reset state inside the guest first:

```powershell
ssh demo
egs-service reset
egs-service run
```

See [Reset and re-run](../howto/reset-and-rerun.md) for fine-grained
flags.

## What's next

- [Write a cloud-config](../howto/write-a-cloud-config.md) — the cloud-config schema fields the agent honors.
- [Configure networking](../howto/configure-networking.md) — static IPs, DNS, MTU via network-config v1/v2.
- [Run shell scripts](../howto/run-shell-scripts.md) — `.ps1` / `.cmd` payloads delivered via fodder.
