using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eryph.GuestServices.Client.ProxyTunnel;

// The on-disk JSON shape, kept deliberately dumb: every field is nullable so the
// loader can tell "absent" from "present but zero/empty" and report a precise
// error, rather than silently accepting a defaulted rule. System.Text.Json source
// generation does not run C# property initializers for absent members, so nullable
// + explicit validation is the only reliable way to require a field here.
//
// UnmappedMemberHandling = Disallow makes an unknown key a hard parse error, which
// is what turns a typo'd or hand-mangled policy into a clear "malformed policy"
// rejection instead of a rule that quietly does nothing.
internal sealed class TunnelPolicyDto
{
    public string? Tunnel { get; set; }
    public string? Catlet { get; set; }

    // Elements are nullable: a JSON array can contain a literal null
    // (e.g. "egress":[null]), which the deserializer surfaces as a null element
    // regardless of the list's element annotation. The loader rejects those.
    public List<EgressRuleDto?>? Egress { get; set; }
    public List<IngressRuleDto?>? Ingress { get; set; }
    public KeepaliveDto? Keepalive { get; set; }
}

internal sealed class EgressRuleDto
{
    public string? Name { get; set; }

    // Guest-side loopback port the guest connects to. A JSON number (e.g. 3300).
    public int? Listen { get; set; }

    // The single host-reachable endpoint egs-tool dials, as "host:port".
    public string? To { get; set; }
}

internal sealed class IngressRuleDto
{
    public string? Name { get; set; }

    // Host-side bind, as "host:port" or a bare port string. Bare port => loopback.
    public string? Listen { get; set; }

    // The single endpoint inside the guest, as "host:port".
    public string? To { get; set; }
}

internal sealed class KeepaliveDto
{
    public int? IdleSeconds { get; set; }
    public int? IntervalSeconds { get; set; }
    public int? Retries { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TunnelPolicyDto))]
internal sealed partial class TunnelPolicyJsonContext : JsonSerializerContext;
