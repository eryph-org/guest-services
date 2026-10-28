# Run a policy-driven proxied tunnel

`egs-tool catlet tunnel` opens a **governed** data path to a catlet over the
eryph-authorized channel. A policy file lists exactly which services may cross, in
each direction, each to a single fixed endpoint — everything else is denied. egs-tool
proxies the bytes itself, so there is no separate forwarder to run and no port is
opened except the ones the policy names.

Use it when a guest must reach **one** external service through the host, or when a
host-side caller must reach **one** service inside the guest, without exposing a
general-purpose tunnel.

## Prerequisites

- The catlet is reachable over the eryph channel (see
  [`egs-tool catlet add-ssh-config`](../reference/cli.md) / `catlet add-key`), the
  same setup the other `egs-tool catlet` commands use.
- The guest's egs-service has **`PortForwardingEnabled`** turned on — this feature is
  gated by the same opt-in switch as `ssh -L` / `-R`. See
  [service settings](../reference/settings.md#service-control). With it off, the
  tunnel is refused.

## The policy file (JSON)

```json
{
  "tunnel": "sap-prod",
  "catlet": "my-app",
  "egress": [
    { "name": "sap-rfc", "listen": 3300, "to": "wass11.corp.example:3300" }
  ],
  "ingress": [
    { "name": "callback", "listen": "127.0.0.1:8080", "to": "127.0.0.1:8080" }
  ],
  "keepalive": { "idleSeconds": 15, "intervalSeconds": 15, "retries": 5 }
}
```

| Field | Meaning |
|---|---|
| `tunnel` | A logical name for the tunnel (used in log output). |
| `catlet` | The catlet id or name to connect to. |
| `egress[]` | The guest reaches out through the host. `listen` is the port opened on the **guest's** loopback; the guest connects there and egs-tool dials the single `to` endpoint on the host network. |
| `ingress[]` | A host-side caller reaches into the guest. `listen` is opened on the **host** (a bare port or `ip:port`; loopback unless you spell out another address); each connection is relayed to the single `to` inside the guest. |
| `to` | Exactly one `host:port`. Wildcards, port ranges and lists are rejected. Write an IPv6 literal as `[address]:port`. |
| `keepalive` | Required. Keeps idle connections and the tunnel itself alive: `idleSeconds` before the first probe, `intervalSeconds` between probes, `retries` before a socket is considered dead. |

Listeners are loopback-bound by default. A non-loopback host bind for an ingress rule
is opt-in — you have to write the address explicitly (e.g. `"0.0.0.0:8080"`).

## Validate it

```
egs-tool catlet tunnel validate --policy sap-prod.json
```

Reports every problem in the file (unknown keys, a wildcard or multi-target `to`, a
missing `keepalive`, a duplicate rule name or listen port) and connects to nothing.

## Run it

```
egs-tool catlet tunnel up --policy sap-prod.json
```

The tunnel runs until you stop it with Ctrl+C. It reconnects automatically if the
channel drops and re-establishes every rule. Connection selectors
(`--configuration`, `--client-id`) and `--identity` work the same as on the other
`egs-tool catlet` commands.

If the guest has `PortForwardingEnabled` off, `tunnel up` is refused rather than
bringing up a partial path.
