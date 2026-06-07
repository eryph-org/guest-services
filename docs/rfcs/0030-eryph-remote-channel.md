# RFC 0030 — Remote access over an eryph-authorized channel

Status: Draft

## Problem

EGS remote access works only on the Hyper-V host. `egs-tool proxy <vmId>`
is a stdin/stdout ↔ Hyper-V-socket bridge that OpenSSH uses as a
`ProxyCommand`; reaching it requires being **logged on to the host as an
administrator**, because opening the guest's Hyper-V socket and writing
the guest's KVP both require host/WMI access. The single authorization
fact today is "you are admin on this host".

That excludes the normal eryph operator, who talks to eryph remotely
through the compute API and has an eryph identity — not a host login.
The just-removed `update-ssh-config` command
(commit `bae2f67`) was a half-step: it used the eryph **system client**
to enumerate **all** catlets and generate `.eryph.alt` aliases, but the
transport was still the local hvsocket. It was admin-on-host,
enumerate-everything, local-only.

We want a remote operator to reach a catlet's egs SSH service through
eryph's **authenticated fabric** — eryph authorizes the channel, the
host agent opens the hvsocket, and SSH runs end-to-end so eryph never
sees plaintext. This RFC covers the **guest-services side**: the
`egs-tool catlet` command group, the guest-side key/expiry changes, and
the libraries eryph consumes. The eryph-side design (compute API
endpoint, operation/saga, agent web host, reverse-proxy) lives in the
eryph repo at `docs/internal/egs-remote-channel.md`; this RFC pins the
contract both sides share.

## Shape

```
egs-tool (operator machine)                 eryph                         host agent            guest
  ssh <catlet>.eryph.alt                  ComputeApi                   (HyperV module)        egs-service
  └ ProxyCommand: egs-tool catlet proxy <catletId>
        │ user creds (existing eryph connection)
        ├──WS (bearer; compute:catlets:remote-access)──►│
        │                                               │ start OpenSshChannel operation
        │                                               │   saga → OpenSshChannelCommand ──►│
        │                                               │     (optional pubkey+expiry)      │ write KVP slot
        │                                               │                                   │ open hvsocket(egs)
        │                                               │◄── result: one-time token ────────│ ready
        │                                               ├──WS(mTLS)+token──────────────────►│ match token→socket
        │◄═══════════════ raw bytes (ciphertext) ═══════╪═══════════════════════════════════╪═hvsocket═►│ sshd
        └ SSH public-key auth, end-to-end, client key ───────────────────────────────────────────────►│
```

eryph reverse-proxies ciphertext only; SSH authenticates the operator's
key directly to the guest. The channel **always** targets the egs
service id (`0000138a-facb-11e6-bd58-64006a7986d3`, vsock port 5002).
Any other guest port is the operator's problem to forward **over** this
SSH session (`ssh -L`), never a second eryph channel.

## `egs-tool catlet` command group

Rebuilt on the same client stack the removed integration used
(`Eryph.ClientRuntime.Configuration`, `Eryph.IdentityModel.Clients`,
`Eryph.ComputeClient`) but with two deliberate changes from the old
`update-ssh-config`:

1. **User credentials, not the system client.** Resolve the configured
   eryph connection (the same one the eryph CLI / PowerShell module
   uses). This is also *why remote works for free*: that stack already
   resolves any configured endpoint — eryph-zero local **or** a remote
   eryph — so nothing in egs-tool is host-bound.
2. **Per-catlet, on demand.** No command enumerates all catlets. The
   bulk `update-ssh-config` is not reintroduced.

| Command | Purpose |
|---|---|
| `egs-tool catlet add-ssh-config <catletId> [--identity <path>] [--add-key]` | Write a single `<catlet>.<project>.eryph.alt` alias whose `ProxyCommand` is `egs-tool catlet proxy <catletId>`. `--identity` selects the private key (BYOK); omitted ⇒ managed key. `--add-key` also runs the key push (flow 2). |
| `egs-tool catlet proxy <catletId>` | Data plane. Authenticate via the user connection, open the WS to the compute API, bridge stdin/stdout. Invoked by SSH `ProxyCommand`; protocol-agnostic. |
| `egs-tool catlet get-client-key` | Print the managed client **public** key (for pasting into a catlet spec / fodder to pre-inject — flow 1). |
| `egs-tool catlet add-key <catletId> [--public-key <path\|->] [--ttl <duration>]` | Push a public key to the catlet's guest via eryph (flow 2). Omitted `--public-key` ⇒ managed key. |
| `egs-tool catlet remove-key <catletId>` | Revoke: eryph clears this operator's KVP slot in the guest. |
| `egs-tool catlet upload-file` / `upload-directory` / `download-file` / `download-directory <catletId> <src> <dst>` | File transfer over the channel. Opens the data plane in-process (same `EryphChannel` open as `proxy`, wrapped as a `WebSocketStream`) and runs an `SshClientSession` over it, then calls the same transport-neutral transfer code the VM-level commands use. `--overwrite` / `--recursive` / `--identity` as on the VM commands. |

The transfer commands share one connector abstraction (`IGuestConnector`)
and one set of copy operations with the VM-level commands; only the
transport — Hyper-V socket vs eryph channel — differs, so the copy
logic is written once.

`add-ssh-config <vmId>` and `<vmId>.hyper-v.alt` (VM-level, local
hvsocket) are unchanged and coexist exactly as the
VM-level/catlet-level split already documents. The `.eryph.alt` suffix
is already reserved in alias validation for this.

### Key custody — BYOK primary, managed fallback

SSH users bring their own key (agent / `~/.ssh`, often a shared
identity; frequently hardware-backed — YubiKey, Secure Enclave, FIDO
`sk-`). A managed-only model cannot serve hardware/agent keys and
fights enterprise PKI policy, so:

- **BYOK is the model.** `--identity` / `--public-key` let the operator
  use their own key; SSH authenticates with their `IdentityFile` or
  agent. eryph stores nothing for the *added* flow — the operator
  supplies the public key at grant time and uses the private key at
  connect time.
- **Managed key is the zero-config fallback** (`ClientKeyHelper`), for
  the operator with no key who wants it to just work.
- Auto-discovery of the matching local key for a *pre-injected* key
  (fingerprint-match against agent) is optional sugar, not in v1.

The two flows differ only in **when** the public key is authorized in
the guest: **pre-injected** = build-time fodder (durable, no expiry);
**added** = runtime KVP push via eryph (carries a TTL, revocable).

## Guest-side changes

### Subject-scoped KVP slots

Today the host writes `eryph:guest-services:client-public-key` and
`…:<id>` named slots, with `<id>` being the host machine name. For
remote access the agent writes a slot per **eryph subject** so multiple
operators coexist and revocation is per-operator:

```
eryph:guest-services:client-public-key:<subject-id>
```

`<subject-id>` is the operator's eryph identity subject (`sub` claim),
URL-safe. This reuses the existing named-slot mechanism in
`ClientKeyProvider` — no new KVP key shape, only a new producer (the
agent instead of host-local `egs-tool`).

### Per-key expiry, enforced in the guest

Runtime-added keys must expire even if eryph/agent later becomes
unreachable, so expiry is enforced **guest-side**, on every auth
attempt, not by a host sweeper. The slot value uses the OpenSSH
`authorized_keys` **`expiry-time`** option, which is already part of
the format we read:

```
expiry-time="20260602143000Z" ssh-ed25519 AAAA… operator@…
```

`ClientKeyProvider` learns to parse leading `authorized_keys` options
and reject a key whose `expiry-time` is in the past. The parser accepts
the OpenSSH compact forms (`yyyyMMdd`, `yyyyMMddHHmm`, `yyyyMMddHHmmss`,
each with an optional trailing `Z`) and ISO-8601/RFC3339; a value with
no offset is read as UTC, and an unparseable `expiry-time` fails closed
(treated as expired). Note the compact form has **no `T` separator** —
`20260602T143000Z` is neither compact nor ISO and is rejected, so
producers must emit `20260602143000Z` (or a full ISO timestamp). Keys without the
option (pre-injected, host-machine slots) are unaffected. Because
`ClientKeyProvider` already re-reads slots on every auth attempt (no
cache), a freshly written key is live immediately and an expired one
stops working without a service restart.

`remove-key` is the explicit-revocation path; expiry is the safety net.

## Libraries consumed by eryph

The eryph agent needs to open the guest hvsocket and write guest KVP —
mechanisms that already exist here and must **not** be re-implemented in
eryph. The following projects become published NuGet packages on the
dbosoft public feed (the same feed eryph already restores
`Eryph.ConfigModel.*` / `Eryph.GenePool.*` from):

| Package | Why eryph needs it |
|---|---|
| `Eryph.GuestServices.Sockets` | `SocketFactory.CreateClientSocket(vmId, serviceId)` + `HyperVEndPoint` — the agent's hvsocket connect. |
| `Eryph.GuestServices.HvDataExchange.Host` | `HostDataExchange` — write the client-key KVP slot via WMI. |
| `Eryph.GuestServices.Core` | `Constants` (egs service id, KVP key names), `PortNumberConverter`. |

These gain packaging metadata (`PackageId`, `IsPackable`,
GitVersion-driven `Version`, license/repo) and a publish step in the
guest-services pipeline. They stay platform-portable as today (Windows
AF_HYPERV + WMI; Linux vsock paths compile but the host-side WMI write
is Windows-only, matching where eryph agents run).

## Threat model

| Adversary | Protected? |
|---|---|
| Network observer between operator and eryph, or eryph and agent | YES — SSH is end-to-end inside the channel; eryph relays ciphertext. mTLS additionally protects the eryph↔agent leg. |
| eryph itself (compromised compute API / controller) | YES for SSH confidentiality — it never holds the SSH key and cannot read the session. It CAN open/deny channels and write KVP keys, so it is trusted for *authorization*, not for *session secrecy*. |
| Operator without `compute:catlets:remote-access` on the project | YES — the compute API rejects the channel before any agent work. |
| Stale grant after access should end | Bounded — added keys carry a TTL enforced guest-side; `remove-key` revokes immediately. Pre-injected keys are durable by design (treat like any provisioned credential). |
| Operator A reading operator B's session | YES — per-subject KVP slots; each operator authenticates with their own key. |

## Non-goals

- Forwarding arbitrary guest ports as distinct eryph channels (use
  `ssh -L` over the egs session).
- Pre-injected-key auto-discovery (fingerprint match) in v1.
- SSH certificates. This is the natural successor — the guest trusts an
  eryph SSH CA (eryph already runs the Component CA), operators present
  short-lived CA-signed certs, and per-key KVP pushes disappear. The
  KVP-pubkey path here is the first step and must not assume individual
  keys are forever.

## Cross-references

- eryph repo `docs/internal/egs-remote-channel.md` — the compute API
  endpoint, `OpenSshChannel` operation/saga/command, agent web host,
  reverse-proxy, mTLS, and the new scope.
- [RFC 0018](0018-ssh-module.md) — the guest SSH server this channel
  reaches.
- Commit `bae2f67` — removal of the prior `update-ssh-config` / eryph
  catlet integration this RFC reworks.
