using System.Net;

namespace Eryph.GuestServices.Client.ProxyTunnel;

// The validated, engine-ready shape of a proxied-tunnel policy file. The raw JSON
// is deserialized into the DTOs in TunnelPolicyJsonContext and then validated and
// parsed into these records by TunnelPolicyLoader, so by the time the engine sees
// a TunnelPolicy every endpoint is parsed, every rule has exactly one fixed target,
// and the keepalive values are positive. Everything not listed is denied: the
// policy is the whole allowlist.
public sealed record TunnelPolicy(
    string Tunnel,
    string Catlet,
    IReadOnlyList<EgressRule> Egress,
    IReadOnlyList<IngressRule> Ingress,
    KeepaliveSettings Keepalive);

// Guest -> out via the host ("gelenkte Netzfreigabe"): the guest connects to
// ListenAddress:ListenPort on its own loopback, and egs-tool dials the single fixed
// ToHost:ToPort on the host network. Realized as an ssh -R registration whose
// forwarded connections egs-tool relays to the fixed target itself.
//
// ListenAddress is a loopback address (default 127.0.0.1). A distinct loopback
// address per target lets the guest's own configuration (an /etc/hosts entry, or
// eryph DNS interception) map the real hostname to that address, so an app can keep
// using the real name while the traffic is governed through this tunnel.
public sealed record EgressRule(
    string Name,
    IPAddress ListenAddress,
    int ListenPort,
    string ToHost,
    int ToPort);

// Host-reachable -> into the guest: egs-tool opens Listen on the host (loopback by
// default) and relays each connection as a direct-tcpip channel to the single
// fixed ToHost:ToPort reachable from inside the guest. Realized as an ssh -L
// forward whose host listener egs-tool owns.
public sealed record IngressRule(
    string Name,
    IPEndPoint Listen,
    string ToHost,
    int ToPort);

// Liveness settings applied to every data-plane socket egs-tool owns (SO_KEEPALIVE
// plus the per-socket idle/interval/retry) and, as the control-channel timeout, to
// the SSH session itself so the tunnel survives a long-idle connection. Required.
public sealed record KeepaliveSettings(
    int IdleSeconds,
    int IntervalSeconds,
    int Retries);
