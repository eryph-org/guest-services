# Azure Wireserver Analysis

Scope: precisely what `168.63.129.16` ("wireserver") does in the Azure VM provisioning flow, who owns which endpoint, and which calls our guest-services agent may safely make. Source for every claim is cited inline.

## TL;DR

Wireserver is a guest-to-fabric control plane spoken in XML over plain HTTP on port 80 (and 32526 for the host-GA-plugin channel). It is **distinct from IMDS** (`169.254.169.254`, JSON) and from DHCP/DNS that also happen to live on `168.63.129.16`. The Ready handshake (`POST /machine?comp=health` with `<Health>...<State>Ready</State>`) is what the fabric controller waits for to declare provisioning successful. On Windows, the Azure VM Agent package splits into the **Provisioning Agent (PA)** and the **Windows Guest Agent (WinGA / `WindowsAzureGuestAgent.exe`)** — the WinGA is the long-running process that owns the health/heartbeat channel after first boot; the PA is the OOBE-stage component that applies the `ovf-env.xml` and is gated by `SetupComplete2.cmd`. ([agent-windows.md](https://learn.microsoft.com/en-us/azure/virtual-machines/extensions/agent-windows), Prereqs + "Azure Marketplace image" sections).

## 1. Endpoint inventory

All endpoints are HTTP (not HTTPS) to `168.63.129.16:80`, require `x-ms-version` (`2012-11-30` for waagent, `2015-04-05` for cloudbase-init) and an agent-name header. Port 32526 is used by the `HostGAPlugin` channel for extension package transport. ([what-is-ip-address-168-63-129-16.md](https://learn.microsoft.com/en-us/azure/virtual-network/what-is-ip-address-168-63-129-16), "Scope" section; [features-windows.md](https://learn.microsoft.com/en-us/azure/virtual-machines/extensions/features-windows), "Network access" section).

| # | Endpoint | Method | Purpose | Request body | Response root |
|---|---|---|---|---|---|
| 1 | `/?comp=versions` | GET | Capability negotiation; smoke test | none | `<Versions><Preferred><Supported>` |
| 2 | `/machine/?comp=goalstate` | GET | Pull current incarnation, ContainerId, RoleInstanceId, and URLs to follow-on resources | none | `<GoalState>` |
| 3 | `/machine?comp=health` | POST | Signal `Ready` / `NotReady` for the role instance (the "fabric Ready handshake") | `<Health>` with `GoalStateIncarnation`, `ContainerId`, `RoleInstanceList/Role/InstanceId`, `Health/State` (`Ready` or `NotReady`), optional `Details/SubStatus/Description` | empty (200) |
| 4 | `/machine?comp=roleProperties` | POST | Report role-instance properties (waagent only) | `<RoleProperties>` | empty |
| 5 | `<HostingEnvironmentConfig>` URL (from goal state) | GET | Deployment / role / VM names | none | `<HostingEnvironmentConfig>` |
| 6 | `<SharedConfig>` URL | GET | Cross-role networking, instance ordering | none | `<SharedConfig>` |
| 7 | `<Certificates>` URL | GET (secure) | Pull encrypted bundle of OSProfile certs (including the cert used for `CustomData` decryption) | none, but the request adds two headers: `x-ms-cipher-name: DES_EDE3_CBC` (or AES128_CBC) and `x-ms-guest-agent-public-x509-cert: <base64 transport cert>` | `<CertificateFile><Data>` (base64 PKCS#7) |
| 8 | `<ExtensionsConfig>` URL | GET | List of extensions to install plus per-extension settings + status-upload SAS blob | none | `<Extensions>` |
| 9 | `<FullConfig>` URL | GET | Full role config (waagent + cloudbase-init read it) | none | `<RoleConfig>` |
| 10 | `/machine?comp=telemetrydata` | POST | Telemetry events from the agent | `<TelemetryData><Provider>` | empty |
| 11 | `/machine?comp=remoteaccessinfo` (referenced as RemoteAccessInfo URI in container) | GET | RDP password reset payload | none | XML |

Sources:
- WALinuxAgent `wire.py` constants `VERSION_INFO_URI`/`HEALTH_REPORT_URI`/`ROLE_PROP_URI`/`TELEMETRY_URI` at lines ~40-44, with `x-ms-agent-name: WALinuxAgent` and `x-ms-version: 2012-11-30` at lines ~1503/44, `Content-Type: text/xml;charset=utf-8` at line ~1510. ([wire.py](https://raw.githubusercontent.com/Azure/WALinuxAgent/master/azurelinuxagent/common/protocol/wire.py))
- WALinuxAgent `goal_state.py` URL `http://{endpoint}/machine/?comp=goalstate` at line ~31; XML elements `Incarnation`, `RoleInstance/InstanceId`, `Container/ContainerId`, `ExtensionsConfig`, `Certificates`, `HostingEnvironmentConfig`, `SharedConfig` at lines ~550-575. ([goal_state.py](https://raw.githubusercontent.com/Azure/WALinuxAgent/master/azurelinuxagent/common/protocol/goal_state.py))
- cloudbase-init `azureservice.py` endpoint table — `comp=Versions` (line ~108), `comp=goalstate` (line ~155), `comp=health` (line ~203), `comp=roleProperties` (line ~217), per-role-instance config URLs (lines ~244-259), certificate fetch with `x-ms-guest-agent-public-x509-cert` (line ~286), with `x-ms-version: 2015-04-05` (line ~36) and `x-ms-guest-agent-name: cloudbase-init` (line ~43). ([azureservice.py](https://raw.githubusercontent.com/cloudbase/cloudbase-init/master/cloudbaseinit/metadata/services/azureservice.py))
- cloud-init `helpers/azure.py` Ready POST `http://{endpoint}/machine?comp=health` at line ~821 with XML template at lines ~627-650 sending `<State>Ready</State>` (success) or `<State>NotReady</State><SubStatus>ProvisioningFailed</SubStatus>` (failure) at lines ~659-664. ([helpers/azure.py](https://raw.githubusercontent.com/canonical/cloud-init/main/cloudinit/sources/helpers/azure.py))

## 2. Per-endpoint use map

| Endpoint | cloud-init `DataSourceAzure` | cloudbase-init `azureservice.py` | WALinuxAgent (long-running) | MS PA on Windows | MS WinGA (`WindowsAzureGuestAgent.exe`) |
|---|---|---|---|---|---|
| `?comp=versions` | smoke-test via helper | yes (line ~108) | yes | likely (capability probe) | likely |
| `/machine/?comp=goalstate` | yes (via helper) | yes (line ~155) | **yes — every incarnation** | yes (first boot) | **yes — continuously** |
| `/machine?comp=health` (Ready) | **yes** — `send_ready_signal` (helpers/azure.py ~763-780) | yes (line ~203) — but skipped on Windows when PA owns the role | yes | **yes — owns first Ready** | **yes — owns ongoing heartbeat** |
| `roleProperties` | no | yes (line ~217) | yes | yes | yes |
| `HostingEnvironmentConfig` | no | yes (line ~244) | yes | possibly | yes |
| `SharedConfig` | no | yes (line ~249) | yes | possibly | yes |
| `Certificates` | yes (decrypts inside helper, line ~474 + ~700-720) | yes (line ~286) | yes | yes (initial OSProfile certs) | yes (ongoing OSProfile sync — see "OSProfile certificates" section of agent-windows.md) |
| `ExtensionsConfig` | no | yes (line ~254) | yes | no | **yes — owns extension handling** |
| `?comp=telemetrydata` | no | no | yes | no | yes |

Citation for "WinGA owns ongoing health + extensions + OSProfile cert sync": [agent-windows.md](https://learn.microsoft.com/en-us/azure/virtual-machines/extensions/agent-windows) — "The Azure VM Agent contains only extension-handling code. The Windows provisioning code is separate" and "If you manually remove these certificates ... the Azure Windows Guest Agent will add them back."

## 3. The wireserver certificate exchange — and what it does NOT cover

The fabric-side cert exchange exists for `AdminPassword` and `LinuxConfigurationSet/SSH` key material — **not** for CustomData. Documenting the exchange anyway because the protocol shape is non-obvious and our previous research note conflated this with CustomData decryption.

Step by step, as implemented in cloud-init `helpers/azure.py` (mirrors waagent):

1. Generate a self-signed RSA cert pair locally — `TransportCert.pem` + `TransportPrivate.pem` (line ~681-682 in `helpers/azure.py`; equivalent OpenSSL call in waagent `goal_state.py`).
2. Fetch goal state; extract the `Certificates` URL from `RoleInstance/Configuration/Certificates`.
3. GET that URL with two extra headers — `x-ms-cipher-name: DES_EDE3_CBC` (or `AES128_CBC`) and `x-ms-guest-agent-public-x509-cert: <base64 of TransportCert.pem>` (lines ~341, ~474). The fabric encrypts the response bundle with the transport public key.
4. Parse `<CertificateFile><Data>` from the response — base64 PKCS#7.
5. Decrypt with the transport **private** key: `openssl cms -decrypt -inkey TransportPrivate.pem -recip TransportCert.pem | openssl pkcs12 -nodes -password pass:` (lines ~700-720).
6. The decrypted PEM bundle is indexed by thumbprint and used to decrypt `AdminPassword` and SSH key payloads whose `<CertificateThumbprint>` references match. **CustomData is NOT in this bundle and has no `CertificateThumbprint` — it is base64-decoded and written verbatim to `C:\AzureData\CustomData.bin`.** See `helpers/azure.py:_parse_property("CustomData", decode_base64=True)` (~line 1282) and the verification note at [research/azure-customdata-encryption.md](azure-customdata-encryption.md) for the per-source confirmation across cloud-init / WALinuxAgent / cloudbase-init / MS docs.

Sources: [helpers/azure.py](https://raw.githubusercontent.com/canonical/cloud-init/main/cloudinit/sources/helpers/azure.py) lines ~341/~465-476/~681-682/~700-720/~1240/~1282; [goal_state.py](https://raw.githubusercontent.com/Azure/WALinuxAgent/master/azurelinuxagent/common/protocol/goal_state.py) lines ~794-840 (cert download + AES128_CBC/DES_EDE3_CBC + `decrypt_certificates_p7m`); [ovfenv.py](https://raw.githubusercontent.com/Azure/WALinuxAgent/master/azurelinuxagent/common/protocol/ovfenv.py) (CustomData read as plain XML text, no cert lookup).

**Key finding for our agent**: CustomData requires no decryption. The local-cert-store path matters only if we ever needed to read encrypted AdminPassword / SSH material, which PA already applies during OOBE — so we don't.

## 4. Ready handshake details

- Method/URL: `POST http://168.63.129.16/machine?comp=health` (NOT PUT — both waagent and cloud-init use POST; the RFC 0008 wording "PUT" is wrong). ([wire.py](https://raw.githubusercontent.com/Azure/WALinuxAgent/master/azurelinuxagent/common/protocol/wire.py) line ~821; [helpers/azure.py](https://raw.githubusercontent.com/canonical/cloud-init/main/cloudinit/sources/helpers/azure.py) line ~821)
- Required headers: `x-ms-agent-name`, `x-ms-version: 2012-11-30`, `Content-Type: text/xml; charset=utf-8`.
- Body (literal template from helpers/azure.py lines ~627-650 and wire.py lines ~627-650):

```xml
<?xml version="1.0" encoding="utf-8"?>
<Health xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
        xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <GoalStateIncarnation>{incarnation}</GoalStateIncarnation>
  <Container>
    <ContainerId>{container_id}</ContainerId>
    <RoleInstanceList>
      <Role>
        <InstanceId>{role_instance_id}</InstanceId>
        <Health>
          <State>Ready</State>
        </Health>
      </Role>
    </RoleInstanceList>
  </Container>
</Health>
```

- `State` values: `Ready` (success) or `NotReady` with `Details/SubStatus = ProvisioningFailed` and a `<Description>` ≤ 512 chars.
- Retry policy in waagent: 30 retries, 15 s delay (wire.py line ~1448).
- Owner on Windows: **PA sends the first Ready during OOBE; WinGA continues sending heartbeats** thereafter. The fabric will mark the VM as failed if no Ready arrives within the OS provisioning timeout (40 min by default for Azure marketplace Windows images; some scenarios cite 60 min, matching the RFC 0008 footnote — Microsoft does not publish a single canonical number but support docs consistently cite 40-90 min depending on the SKU/extension flow, e.g. [troubleshoot/windows-azure-guest-agent](https://learn.microsoft.com/en-us/troubleshoot/azure/virtual-machines/windows/windows-azure-guest-agent)).

Verification against cbi: `azureservice.py` sends `comp=health` at line ~203 and is structured so that on Windows the Ready signal is only sent when PA is absent. Cbi never targets Azure Windows VMs as primary provisioner — Microsoft's recommendation is to disable cbi's OVF provisioning on Azure and let PA run ([learn.microsoft.com/answers/questions/2244595](https://learn.microsoft.com/en-us/answers/questions/2244595/cloudbase-init-running-in-windows-azure-vm)).

## 5. cbi `azureservice.py` wireserver call inventory

Endpoints (file-line citations to [azureservice.py](https://raw.githubusercontent.com/cloudbase/cloudbase-init/master/cloudbaseinit/metadata/services/azureservice.py)):
- `comp=Versions` — line ~108 (`get_versions`)
- `comp=goalstate` — line ~155 (`_get_goal_state`)
- `comp=health` POST — line ~203 (`_post_health_status`)
- `comp=roleProperties` POST — line ~217 (`_post_role_properties`)
- Per-RoleInstance config URLs (hosting env / shared config / extensions config / full config) — lines ~244-259
- Certificates GET with transport cert in `x-ms-guest-agent-public-x509-cert` — line ~286
- CustomData read — `get_user_data()` at line ~434, but reads from the ConfigDrive (`CustomData.bin`), **not** wireserver. Decryption uses certs already imported by the cert-fetch step.

## 6. cloud-init `DataSourceAzure` wireserver call inventory

`DataSourceAzure.py` does no direct HTTP — it calls helpers in `cloudinit/sources/helpers/azure.py`:
- `get_metadata_from_fabric()` (lines ~856-867 in helpers) — orchestrates goal-state fetch + cert fetch + Ready POST, returns SSH key fingerprints; invoked from `_report_ready()` in `DataSourceAzure.py` lines ~1910-1930.
- `report_failure_to_fabric()` (lines ~870-877) — POSTs `NotReady` with encoded error; invoked from `_report_failure()` lines ~1800-1870.
- Endpoint default `DEFAULT_WIRESERVER_ENDPOINT = "168.63.129.16"` (line ~29 of helpers); overridable via DHCP option 245 (`DataSourceAzure.py` line ~803).
- `imds.py` does **not** touch wireserver — IMDS only (`169.254.169.254`).

## 7. Side effects of NOT calling wireserver

If our agent never touches wireserver:
- **CustomData**: nothing to decrypt. PA base64-decodes the `<CustomData>` value out of ovf-env and writes the resulting bytes to `C:\AzureData\CustomData.bin`. Our agent reads those bytes directly. (For encrypted AdminPassword / SSH key material the OSProfile certs are already imported into `Cert:\LocalMachine\My` by PA and re-imported by WinGA if removed, but we never need them — PA applied those fields during OOBE.)
- **Extensions delivery**: unaffected. Extensions are fetched and applied by the **WinGA** (`WindowsAzureGuestAgent.exe`), which is a separate, long-running service that continues to drive the goal-state loop in the background regardless of our agent ([agent-windows.md](https://learn.microsoft.com/en-us/azure/virtual-machines/extensions/agent-windows): "The Azure VM Agent contains only extension-handling code. The Windows provisioning code is separate").
- **Fabric tearing the VM down**: would only happen if **PA fails to send the initial Ready**. PA owns that step; our agent's silence is irrelevant. We never become the timeout's responsible party — PA is.
- **Soft features**: load-balancer health probes use a separate inbound channel from `168.63.129.16` to the VM (not initiated by us); DNS, DHCP, and KVP heartbeats are not our concern. **Telemetry** posts from WinGA continue; our agent emitting no telemetry simply means Microsoft support has less to look at — no functional impact.

## 8. Coexistence reality check

On a marketplace Windows VM:
- **PA** runs during `oobeSystem` (Sysprep specialize→OOBE transition), applies `ovf-env.xml`, writes `C:\AzureData\CustomData.bin`, **sends the first `<State>Ready</State>` POST**, and exits. PA is staged via `Unattend.xml` (`FirstLogonCommands` / `SetupComplete2.cmd`); after OOBE it is not a long-running service.
- **WinGA** (`WindowsAzureGuestAgent.exe`) is a Windows service that **stays running**. It owns the ongoing goal-state polling loop, heartbeat health POSTs, extension installation, OSProfile cert sync, telemetry, and the host-GA-plugin channel on 32526. It will continue running after our agent starts and forever after.
- `SetupComplete2.cmd` is the standard hook MS PA writes for follow-on provisioners (cloudbase-init / our agent) — when our service starts via this hook, PA's Ready has already happened and WinGA is already running.

Sources: [agent-windows.md](https://learn.microsoft.com/en-us/azure/virtual-machines/extensions/agent-windows) ("Azure Marketplace image" + "Detect the Azure Windows VM Agent" + "Azure Windows Guest Agent and OSProfile certificates" sections); [Microsoft Q&A on cbi on Azure](https://learn.microsoft.com/en-us/answers/questions/2244595/cloudbase-init-running-in-windows-azure-vm) confirms cbi must disable its own OVF provisioning when PA is present.

## 9. Recommendation

**Default stance: skip wireserver entirely.** Our agent has no business calling any wireserver endpoint in v1. Justification:

| Endpoint | Our agent | Reason |
|---|---|---|
| `?comp=versions` | skip | not useful; no decision depends on it |
| `goalstate` | skip | WinGA already pulls it; we have no consumer for the data |
| `health` | **NEVER** | PA + WinGA own this; a second writer risks fabric confusion (per RFC 0008) |
| `roleProperties` | skip | identifies the role-instance to the fabric — not our identity |
| `hostingEnvironmentConfig` / `sharedConfig` / `fullConfig` | skip — IMDS gives us the same data | IMDS `compute.*` is the JSON modern equivalent |
| `certificates` | skip | PA + WinGA already import the certs locally; we read from `Cert:\LocalMachine\My` |
| `extensionsConfig` | skip | WinGA owns extensions |
| `telemetrydata` | **NEVER** | identifying as a Microsoft agent in telemetry pollutes MS support data |
| `remoteaccessinfo` | skip | RDP password reset is a WinGA / VMAccess-extension flow |

The only conceivable read-only call would be `GET /?comp=versions` as a sanity probe — but we already have IMDS and registry+chassis-tag probes for "are we on Azure", so even this is unnecessary noise.

## Corrections to RFC 0014

1. **HTTP method for Ready is POST, not PUT** (RFC 0014 §"Coexistence with PA" says "PUT to `/machine?comp=health`"; actual method is POST per [wire.py](https://raw.githubusercontent.com/Azure/WALinuxAgent/master/azurelinuxagent/common/protocol/wire.py) line ~821 and [helpers/azure.py](https://raw.githubusercontent.com/canonical/cloud-init/main/cloudinit/sources/helpers/azure.py) line ~821). RFC 0008 has the same error in its provisioning-success-signal column.
2. **PA is not the only Microsoft component holding the wireserver channel.** RFC 0014 implies "PA owns the wireserver"; the more accurate model is "PA owns the **first** Ready; WinGA owns the **ongoing** wireserver traffic indefinitely". This strengthens the no-touch recommendation — the channel is never idle, it is always owned by some MS component.
3. **CustomData is not encrypted at all.** RFC 0014 §"Deferred to RFC 0015" assumed a decryption step was needed; there is no PKCS#7 envelope to decrypt. PA base64-decodes the ovf-env `<CustomData>` value and writes the bytes verbatim. RFC 0015 (CustomData decryption) is therefore unnecessary and should be dropped. See [research/azure-customdata-encryption.md](azure-customdata-encryption.md) for the source-by-source verification.
4. **Azure x-ms-version**: RFC 0014 does not mention this header. If we ever do touch wireserver for diagnostic purposes only, the well-known versions are `2012-11-30` (waagent) and `2015-04-05` (cbi); newer is not necessarily supported on the fabric side.
