# RFC 0034 — Policy-driven proxied tunnel in egs-tool

Status: Draft

## Problem

egs-tool can already reach a catlet's egs SSH service over the eryph-authorized
channel (RFC 0030), and the guest service can be told to allow SSH port forwarding
(`-L` / `-R`) over that channel (PR #75, gated by `PortForwardingEnabled`). But the
forwarding itself is done by the **external OpenSSH client**: egs-tool only supplies
a stdin/stdout↔socket pipe as a `ProxyCommand`. Standing up a *governed* data path —
"let this guest reach exactly this one host service, and nothing else" — therefore
needs manual glue: an `ssh -L`/`-R` invocation **plus** a separate forwarder process
that dials a fixed endpoint, with ports wired by hand. RFC 0030 explicitly left
"forwarding arbitrary guest ports as distinct eryph channels" a non-goal and pointed
at `ssh -L` over the session; nothing packaged that into a first-class, auditable
feature.

The need is concrete: a guest (e.g. an SAP application server) must reach one
external endpoint through the host ("gelenkte Netzfreigabe" — a governed network
release), and/or a host-side caller must reach one service inside the guest — with a
default-deny posture, one fixed target per rule, and a connection that survives long
idle periods.

## Goal

A first-class **proxied tunnel** in egs-tool, driven by a **policy file**, that
replaces the tunnel-plus-external-forwarder glue with one authenticated, in-process
operation. egs-tool owns both the eryph/SSH channel and the data-plane sockets: it
proxies the bytes itself, opening no listener beyond what the policy declares and no
separate forwarder process. Gated by `PortForwardingEnabled` exactly like `-R`.

The feature is generic: no SAP-, product- or deployment-specific knowledge lives in
egs-tool. The policy file (which endpoints, which services) is supplied by the
consumer; egs-tool only enforces the mechanism.

## Policy file (JSON)

One file describes, per tunnel, the exact set of **ingress** and **egress** services
allowed. Everything not listed is denied. Each rule names exactly ONE fixed target —
the single allowed endpoint is the control; the guest cannot choose arbitrary
destinations.

```json
{
  "tunnel": "sap-prod",
  "catlet": "<catlet-id-or-name>",
  "egress": [
    { "name": "sap-rfc", "listen": 3300, "listenAddress": "127.0.0.2", "to": "wass11.corp.example:3300" }
  ],
  "ingress": [
    { "name": "saphttp-callback", "listen": "127.0.0.1:8080", "to": "127.0.0.1:8080" }
  ],
  "keepalive": { "idleSeconds": 15, "intervalSeconds": 15, "retries": 5 }
}
```

- **egress** — the guest reaches an external service *only through the host*. The
  guest-side `listen` port is opened on the guest's loopback; egs-tool dials the
  single `to` endpoint on the host network and relays the bytes. Realized as an SSH
  reverse forward (`tcpip-forward`) whose forwarded connections egs-tool relays to
  the fixed target itself. An optional `listenAddress` (default `127.0.0.1`, must be
  a loopback literal such as `127.0.0.2`) lets the policy declare a distinct
  guest-side bind per target, so the guest's own configuration can map a real
  hostname to that address — an `/etc/hosts` entry today, or eryph DNS interception
  later — and an app keeps using the real name while the traffic stays governed. The
  guest's loopback pin honors the `127.0.0.0/8` range for this, while still forcing
  any non-loopback bind back to loopback.
- **ingress** — a host-side caller reaches a single service inside the guest.
  egs-tool opens `listen` on the host (loopback unless an explicit non-loopback IP is
  written) and relays each connection to the single `to` inside the guest, as an SSH
  direct-tcpip channel.
- Each rule is one fixed `to`. No wildcards, no port ranges, no lists, no
  guest-chosen destination. These are rejected at parse time.
- `keepalive` is required (see idle-stability below).

JSON (not YAML) is the chosen format: egs-tool already uses System.Text.Json with
source-generated, trim/AOT-safe contexts, and a flat JSON object is simpler for a
consumer to template than YAML with its scalar-style quirks.

## CLI

```
egs-tool catlet tunnel up       --policy <file>   # run the tunnel until stopped
egs-tool catlet tunnel validate --policy <file>   # validate without connecting
```

`tunnel` is a sub-branch of the existing `catlet` command group, honoring the same
eryph auth / `.eryph.alt` model (`--configuration`, `--client-id`, `--identity`).
The verb is `tunnel`, not `proxy`: `egs-tool catlet proxy <catletId>` already exists
as the SSH `ProxyCommand` stdin/stdout bridge (RFC 0030) and is unchanged.

## Behavior

- **In-process proxy.** egs-tool runs an `SshClientSession` over the eryph channel
  and activates the DevTunnels `PortForwardingService`. It owns the data plane: for
  ingress it opens its own host `TcpListener` and relays to the guest; for egress it
  registers a guest-side loopback listener and dials the `to` target with its own
  socket. No external forwarder; no listener beyond the policy.
- **Idle-stability.** A quiet connection must not be torn down. egs-tool sets TCP
  keepalive (`SO_KEEPALIVE` + the policy's idle/interval/retry) on every data-plane
  socket it owns, and sets the SSH session's control-channel keepalive
  (`KeepAliveTimeoutInSeconds`) so the tunnel itself survives long idle.
- **Long-running.** On a dropped session egs-tool tears down the forwards and
  re-establishes the whole policy (bounded backoff); a clean stop (Ctrl+C) closes
  the listeners and sockets and cancels the reverse-forward registrations.
- **Fail closed.** A rule that cannot be established — e.g. `tcpip-forward` denied
  because `PortForwardingEnabled` is off — fails; egs-tool never falls back to an
  open or wider forward. A startup gate probe (an ephemeral reverse forward) refuses
  the whole tunnel up front when port forwarding is off, for ingress-only policies
  too.
- **Bounded resource use.** Each rule admits at most a fixed number of concurrent
  data-plane connections (128); beyond that, new connections are refused. This caps a
  malicious guest's ability to amplify connections through the host against the fixed
  egress target. A relay is also torn down when its SSH channel closes (not merely on
  a one-direction EOF), so a half-closed connection against a target that holds its
  socket open cannot accumulate — while a legitimately half-closed caller still
  receives its full response.

## Client-side channel pinning (threat model)

**The whole VM is untrusted — including egs-service (the guest SSH server).** The
tunnel's job is to let a guest reach exactly the declared services and no more, so
every "which services are open" decision is enforced **host-side in egs-tool** and
nothing is delegated to the guest:

- egs-tool opens **only** the listeners the policy declares (ingress on the host;
  egress is a guest-side loopback listener the guest cannot widen).
- For egress, egs-tool dials **only** the policy's fixed `to`. The `forwarded-tcpip`
  channel a connection arrives on carries a guest-supplied originator address;
  egs-tool ignores it and dials the policy target, so a malicious guest cannot
  redirect the dial.
- The single-fixed-target guarantee is therefore a **host-side** property; it does
  not depend on the guest constraining anything.

egs-tool is the SSH *client*; the guest is the server. A compromised or malicious
guest must not be able to steer egs-tool into opening data paths the policy did not
declare. The DevTunnels `PortForwardingService` defaults are therefore overridden
before any forward is established:

| Property | Set to | Why |
|---|---|---|
| `AcceptRemoteConnectionsForNonForwardedPorts` | `false` | Never accept a guest-initiated `direct-tcpip` channel to an arbitrary host-local port. |
| `AcceptLocalConnectionsForForwardedPorts` | `false` | No library-owned listener; ingress listeners are opened by egs-tool, loopback-bound unless the policy opts out. |

`forwarded-tcpip` channels for the ports egs-tool *did* forward (the egress rules)
still work regardless of these settings — that is the only remote-initiated path,
and it is bounded to the policy's declared ports.

For egress, egs-tool uses a `RemotePortStreamer` (`StreamFromRemotePortAsync`) and
dials the rule's fixed `to` itself from the `StreamOpened` handler, and it verifies
the port the guest bound matches the one requested.

`ForwardConnectionsToLocalPorts` is deliberately left at its **default (true)** — the
streamer's delivery path depends on it. This is *not* defense in depth: with it true
the library would, for a guest-initiated `direct-tcpip` channel, dial the
guest-supplied `host:port` from the host. The single control preventing that is
`AcceptRemoteConnectionsForNonForwardedPorts = false`, which rejects the channel
before any dial. That one pin must never be relaxed, and any future second session
path must set it too.

| Adversary | Protected? |
|---|---|
| Guest opening channels to host ports the policy never declared | YES — `AcceptRemoteConnectionsForNonForwardedPorts = false`. |
| Guest redirecting an egress dial to a different host | YES — egs-tool dials the rule's fixed `to`, never a guest-supplied address. |
| Operator reaching a guest service the policy never declared | YES — only the declared ingress `to` is reachable; default-deny. |
| Network observer / eryph itself | As RFC 0030 — SSH is end-to-end inside the channel; eryph relays ciphertext. |

## Non-goals

- Changing the existing `-L`/`-R` or `catlet proxy` behavior (this is additive).
- Changing the loopback-bound-by-default stance (PR #75).
- Any product- or deployment-specific knowledge in egs-tool.

## Relationship to RFC 0030 / PR #75

This is the productized form of "`-L`/`-R` over the egs session" that RFC 0030 named
as the model and PR #75 enabled server-side. The guest side is unchanged: the same
`PortForwardingEnabled` gate, the same loopback pin, the same advertised
`port-forwarding` feature. Only the host side changes — egs-tool replaces external
OpenSSH + a hand-wired forwarder with one in-process, policy-bounded operation.
