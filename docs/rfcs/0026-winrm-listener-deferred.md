# RFC 0026 — WinRM listener / certificate auth (DEFERRED)

Status: Draft

## Problem

cloudbase-init exposed two WinRM-related plugins for the
Azure / Server 2016 era: one to configure the WinRM HTTPS listener
(creating a self-signed cert, opening the firewall, enabling the
service), and one to wire WinRM certificate-based authentication so
operators could `Enter-PSSession` with a client cert. Cloud-config
operators from that ecosystem may expect parity from egs-service.

## What cloud-init does

Nothing. WinRM is Windows-specific; cloud-init never grew a module
for it.

## What cloudbase-init does

Two plugins under `cloudbaseinit/plugins/windows`:

- `winrmlistener.py` — enables WinRM, creates an HTTPS listener with
  an auto-generated certificate, opens TCP/5986, sets the service
  startup to Automatic.
- `winrmcertificateauth.py` — imports a user-supplied client cert into
  `LocalMachine\TrustedPeople`, then runs `winrm create
  winrm/config/service/certmapping` to bind the cert to a local user
  for cert-based PSRemoting.

Source:
<https://github.com/cloudbase/cloudbase-init/tree/master/cloudbaseinit/plugins/windows>

## What surprised us about the cbi plugins

The cert-mapping plugin is fiddly in a way that's easy to under-design:
it doesn't just import the cert and run a `winrm` command — it has to
discover the cert's thumbprint after import (the API doesn't return
it directly), match it against the operator's username, and write the
`certmapping` rule with the *issuer thumbprint*, not the client cert
thumbprint. Several real-world failures we've seen on cbi tickets are
issuer-vs-client thumbprint confusion. Any future implementation needs
to surface that distinction in the schema explicitly.

The listener plugin is comparatively boring: enable service, create
self-signed cert via `New-SelfSignedCertificate`, `winrm quickconfig
-transport:https`, firewall rule. The catch is the cert renewal
lifecycle — cbi creates the cert ONCE; nothing renews it. Genes that
need long-lived WinRM access typically supply their own cert via
fodder and the cbi plugin gets out of the way.

## Why deferred

Microsoft is steering Windows toward OpenSSH for remote management:

- **Server 2025 ships OpenSSH by default** (Optional Feature pre-staged
  and enabled on Server Core).
- **PowerShell 7.4+ defaults `Invoke-Command` over SSH transport** when
  the target hostname doesn't match a configured WinRM endpoint;
  Microsoft's docs increasingly position SSH as the preferred remoting
  transport for cross-platform fleets.
- **eryph users already get SSH** over the Hyper-V socket via
  egs-service itself — no WinRM is needed for the egs-tool /
  agent-control plane.

WinRM still matters for:

- Legacy Windows Server 2016 / 2019 with Group Policy and tooling
  already plumbed through WinRM.
- Specific Microsoft tooling (SCCM, some MDM agents) that requires
  WinRM.

But for both cases, operators today can run the listener / cert
mapping from a `runcmd` block — the commands are short:

```yaml
runcmd:
  - powershell -Command "Enable-PSRemoting -Force"
  - powershell -Command "New-NetFirewallRule -Name WinRM-HTTPS -DisplayName 'WinRM HTTPS' -Protocol TCP -LocalPort 5986 -Action Allow"
```

so the missing module isn't blocking anyone.

We will revisit when a concrete consumer says "we need WinRM cert auth
configured by user-data and runcmd isn't enough" — at that point the
cert-mapping schema (which is the genuinely hard part) can be designed
against a real use case rather than guessed.

## What would change our minds

- Microsoft re-prioritising WinRM (unlikely — direction of travel is
  clear).
- A class of catlets that ship without `runcmd`-capable fodder but DO
  need WinRM listener configuration.
- A user surfacing a workflow where the issuer-vs-client thumbprint
  dance in `winrmcertificateauth.py` is load-bearing for them.

## Open questions (for the revisit)

- Should a future module reuse the egs-service self-signed cert that
  SSH already uses, or generate a separate WinRM-only cert? Reusing is
  smaller surface but couples two protocols' rotation lifecycles.
- HTTP listener support (TCP/5985) — cbi exposed it, real eryph
  catlets should never enable it. Probably leave HTTP unsupported even
  in a future implementation.

## Cross-references

- [RFC 0018](0018-ssh-module.md) — Windows OpenSSH daemon
  configuration, the modern alternative we encourage instead of WinRM.
- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — `runcmd` /
  `scripts/per-instance` cover the WinRM-bootstrap use case today.
